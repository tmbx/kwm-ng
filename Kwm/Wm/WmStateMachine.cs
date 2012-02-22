using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace kwm
{
    /// <summary>
    /// This class is a pure data store that contains information about the
    /// KCD ANP messages processed by the workspace manager.
    /// </summary>
    public static class WmKcdState
    {
        /// <summary>
        /// Number of events to process between each quench check.
        /// </summary>
        public const UInt32 QuenchBatchCount = 100;

        /// <summary>
        /// Rate at which events will be processed, e.g. 1 message per 2
        /// milliseconds.
        /// </summary>
        public const UInt32 QuenchProcessRate = 5;

        /// <summary>
        /// True if event processing is currently quenched.
        /// </summary>
        public static bool QuenchFlag = false;

        /// <summary>
        /// Number of events that have been processed in the current batch.
        /// </summary>
        public static UInt32 CurrentBatchCount = 0;

        /// <summary>
        /// Date at which the current batch has been started.
        /// </summary>
        public static DateTime CurrentBatchStartDate = DateTime.MinValue;

        /// <summary>
        /// True if the broker has notified us that an event occurred.
        /// </summary>
        public static bool EventFlag = false;

        /// <summary>
        /// ID of the next ANP command message.
        /// </summary>
        public static UInt64 NextKcdCmdID = 1;
    }

    /// <summary>
    /// This class represents the state machine of the workspace manager. The
    /// state machine coordinates the state transitions of the workspace
    /// manager.
    /// </summary>
    public static class WmSm
    {
        /// <summary>
        /// Interval in seconds between WM serializations.
        /// </summary>
        private const UInt32 WmSerializationDelay = 5 * 60;

        /// <summary>
        /// This timer is used to wake-up the state machine when it needs to
        /// run.
        /// </summary>
        private static KWakeupTimer m_wakeupTimer = new KWakeupTimer();

        /// <summary>
        /// Date at which the WM was last serialized.
        /// </summary>
        private static DateTime m_lastSerializationDate = DateTime.MinValue;

        /// <summary>
        /// True if the state machine is running.
        /// </summary>
        private static bool m_runningFlag = false;

        /// <summary>
        /// Date at which the state machine needs to run again.
        /// MinValue: the state machine needs to run at the first
        ///           opportunity.
        /// MaxValue: the state machine does not need to run.
        /// </summary>
        private static DateTime m_nextRunDate = DateTime.MaxValue;

        /// <summary>
        /// Queue of workspace notifications that are pending delivery.
        /// </summary>
        private static Queue<KwsSmNotif> m_notifQueue = new Queue<KwsSmNotif>();

        /// <summary>
        /// Number of calls to LockNotif() that were not yet matched by a call
        /// to UnlockNotif().
        /// </summary>
        private static int m_notifLockCount = 0;

        /// <summary>
        /// True if the workspace manager is stopped or stopping.
        /// </summary>
        public static bool StopFlag
        {
            get { return Wm.MainStatus == WmMainStatus.Stopped || Wm.MainStatus == WmMainStatus.Stopping; }
        }

        public static void Relink()
        {
            m_wakeupTimer.TimerWakeUpCallback = HandleTimerWakeUp;
            m_lastSerializationDate = DateTime.Now;
        }

        /// <summary>
        /// This method is called to stop the WM. It returns true when the WM
        /// is ready to stop.
        /// </summary>
        private static bool TryStop()
        {
            // Ask the workspaces to stop.
            bool allStopFlag = true;
            foreach (Workspace kws in Wm.KwsTree.Values)
                if (!kws.Sm.TryStop()) allStopFlag = false;

            // Ask the brokers to stop.
            if (!Wm.KcdBroker.TryStop()) allStopFlag = false;
            if (!Wm.KmodBroker.TryStop()) allStopFlag = false;
            if (!Wm.EAnpBroker.TryStop()) allStopFlag = false;

            // All KCDs must be disconnected.
            foreach (WmKcd kcd in Wm.KcdTree.Values)
                if (kcd.ConnStatus != KcdConnStatus.Disconnected) allStopFlag = false;

            // The UI must not have been reentered.
            if (WmUi.UiEntryCount > 0) allStopFlag = false;

            return allStopFlag;
        }

        /// <summary>
        /// This method instructs the timer thread to send us an event at next
        /// run date so that the state machine can run at the proper time.
        /// </summary>
        private static void ScheduleTimerEvent()
        {
            Int64 ms = -1;

            if (m_nextRunDate != DateTime.MaxValue)
            {
                DateTime now = DateTime.Now;
                if (m_nextRunDate < now) ms = 0;
                else ms = (Int64)(m_nextRunDate - now).TotalMilliseconds;
            }

            m_wakeupTimer.WakeMeUp(ms);
        }

        /// <summary>
        /// Return true if the state machine wants to run now.
        /// </summary>
        private static bool WantToRunNow()
        {
            // Fast track.
            if (m_nextRunDate == DateTime.MinValue) return true;

            // System call path.
            return (m_nextRunDate <= DateTime.Now);
        }

        /// <summary>
        /// Run the workspace manager state machine.
        /// </summary>
        private static void Run(String who)
        {
            KLogging.Log("WmSm: Run() called by " + who);

            try
            {
                // Avoid reentrance.
                if (m_runningFlag)
                {
                    KLogging.Log("WmSm: already running, bailing out.");
                    return;
                }

                m_runningFlag = true;

                // Loop until our state stabilize.
                LockNotif();
                while (WantToRunNow()) RunPass();
                UnlockNotif();

                // Schedule the next timer event appropriately.
                ScheduleTimerEvent();

                // We're no longer running the WM.
                m_runningFlag = false;
            }

            // We cannot recover from these errors.
            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Run once through the bowels of the state machine.
        /// </summary>
        private static void RunPass()
        {
            // Reset the next run date to the maximum. During the processing of this
            // method, the next run date will lower itself as needed.
            m_nextRunDate = DateTime.MaxValue;

            // We're stopped, nothing to do.
            if (Wm.MainStatus == WmMainStatus.Stopped)
            {
                KLogging.Log("WmSm: " + KwmStrings.Kwm + " is stopped, nothing to do.");
            }

            // Try to stop.
            else if (Wm.MainStatus == WmMainStatus.Stopping && TryStop())
            {
                HandlePostStop();
            }

            // Perform regular processing.
            else
            {
                // Serialize the WM if needed.
                SerializeWmIfNeeded();

                // Process the workspace state machines.
                ProcessKwsStateMachines();

                // Process the workspaces to remove.
                ProcessKwsToRemove();

                // Process the KCDs state changes.
                ProcessKcdState();

                // Recompute the KCD event processing quench.
                RecomputeKcdQuenching();

                // Process the KCD messages.
                ProcessKcdMessages();
            }
        }

        /// <summary>
        /// This method is called when the workspace manager has stopped.
        /// The WM is serialized and the application is told to quit.
        /// </summary>
        private static void HandlePostStop()
        {
            KLogging.Log("WmSm: " + KwmStrings.Kwm + " has stopped.");
            Wm.MainStatus = WmMainStatus.Stopped;
            Wm.Serialize();
            Program.RequestAppExit();
        }

        /// <summary>
        /// Process the workspaces to remove, once.
        /// </summary>
        private static void ProcessKwsToRemove()
        {
            // Fast track.
            if (Wm.KwsRemoveTree.Count == 0) return;

            SortedDictionary<UInt64, Workspace> tree = new SortedDictionary<UInt64, Workspace>(Wm.KwsRemoveTree);
            foreach (Workspace kws in tree.Values)
                if (Wm.KwsRemoveTree.ContainsKey(kws.InternalID)) ProcessOneKwsToRemove(kws);
        }

        /// <summary>
        /// Process one workspace to remove.
        /// </summary>
        private static void ProcessOneKwsToRemove(Workspace kws)
        {
            // We can remove the workspace now.
            if (kws.Sm.ReadyToRemove())
            {
                // Remove the workspace from the manager.
                Wm.RemoveWorkspace(kws);

                // Request a run of the state machine in case we want to stop.
                RequestRun("removed " + KwmStrings.Kws);
            }
        }

        /// <summary>
        /// Process the KCDs state changes. Connects KCDs that need
        /// to be reconnected.
        /// The loop could be optimized with a priority queue.
        /// </summary>
        private static void ProcessKcdState()
        {
            foreach (WmKcd kcd in Wm.KcdTree.Values)
            {
                // The KCD should be disconnecting if nobody is using it.
                Debug.Assert(kcd.KwsConnectTree.Count > 0 ||
                             kcd.ConnStatus == KcdConnStatus.Disconnected ||
                             kcd.ConnStatus == KcdConnStatus.Disconnecting);

                // We want the KCD to be connected.
                if (kcd.ConnStatus == KcdConnStatus.Disconnected &&
                    kcd.KwsConnectTree.Count > 0)
                {
                    DateTime deadline = kcd.GetReconnectDeadline();

                    // Time to try again.
                    if (deadline <= DateTime.Now) ConnectKcd(kcd);

                    // Try again later.
                    else ScheduleRun("KCD automatic reconnection", deadline);
                }
            }
        }

        /// <summary>
        /// Serialize the WM if it is time to do so.
        /// </summary>
        private static void SerializeWmIfNeeded()
        {
            DateTime now = DateTime.Now;

            if (m_lastSerializationDate.AddSeconds(WmSerializationDelay) < now)
            {
                m_lastSerializationDate = now;
                Wm.Serialize();
            }

            ScheduleRun("WM serialization", m_lastSerializationDate.AddSeconds(WmSerializationDelay));
        }

        /// <summary>
        /// Run the workspace state machines that are ready to run, once.
        /// Also update the WM state machine next run date appropriately.
        /// The loop could be optimized with a priority queue, if needed.
        /// </summary>
        private static void ProcessKwsStateMachines()
        {
            DateTime now = DateTime.Now;
            foreach (Workspace kws in Wm.KwsTree.Values)
            {
                if (kws.Sm.NextRunDate <= now) kws.Sm.Run();
                if (kws.Sm.NextRunDate < m_nextRunDate) m_nextRunDate = kws.Sm.NextRunDate;
            }
        }

        /// <summary>
        /// Recompute the value of the KCD event processing quench.
        /// </summary>
        private static void RecomputeKcdQuenching()
        {
            // Batch check count not yet reached.
            if (WmKcdState.CurrentBatchCount < WmKcdState.QuenchBatchCount)
            {
                Debug.Assert(WmKcdState.QuenchFlag == false);
                return;
            }

            // Compute deadline.
            DateTime deadline = WmKcdState.CurrentBatchStartDate.AddMilliseconds(WmKcdState.CurrentBatchCount * WmKcdState.QuenchProcessRate);
            DateTime now = DateTime.Now;

            // Enough time has passed during the processing of the batch.
            // Reset the batch statistics.
            if (deadline < now)
            {
                WmKcdState.CurrentBatchCount = 0;
                WmKcdState.CurrentBatchStartDate = now;

                // We were previously quenched.
                if (WmKcdState.QuenchFlag)
                {
                    // We're no longer quenched.
                    WmKcdState.QuenchFlag = false;

                    // Run every workspace state machine.
                    RequestAllKwsRun("KCD event processing unquenched");
                }
            }

            // Quench for a bit.
            else
            {
                WmKcdState.QuenchFlag = true;
                ScheduleRun("KCD event processing quenched", deadline);
            }
        }

        /// <summary>
        /// Process the messages received from the KCD, if any.
        /// </summary>
        private static void ProcessKcdMessages()
        {
            // If we were not notified, bail out quickly.
            if (!WmKcdState.EventFlag) return;

            // Clear the notification flag.
            WmKcdState.EventFlag = false;

            // Get the messages.
            List<KcdControlMsg> controlList = new List<KcdControlMsg>();
            List<KcdAnpMsg> anpList = new List<KcdAnpMsg>();
            Wm.KcdBroker.GetMessagesForWm(out controlList, out anpList);
            KLogging.Log("ProcessKcdMessages(), anpList.Count = " + anpList.Count);

            // Process the messages.
            foreach (KcdControlMsg m in controlList) ProcessKcdControlMsg(m);
            foreach (KcdAnpMsg m in anpList) ProcessKcdAnpMsg(m);
        }

        /// <summary>
        /// Process an control messages received from the KCD.
        /// </summary>
        private static void ProcessKcdControlMsg(KcdControlMsg m)
        {
            // Dispatch the message.
            if (m is KcdConnectionNotice) ProcessKcdConnectionNotice((KcdConnectionNotice)m);
            else if (m is KcdDisconnectionNotice) ProcessKcdDisconnectionNotice((KcdDisconnectionNotice)m);
            else throw new Exception("unexpected KCD control message received");
        }

        /// <summary>
        /// Process a KCD connection notice.
        /// </summary>
        private static void ProcessKcdConnectionNotice(KcdConnectionNotice notice)
        {
            Debug.Assert(Wm.KcdTree.ContainsKey(notice.KcdID));
            WmKcd kcd = Wm.KcdTree[notice.KcdID];
            Debug.Assert(kcd.ConnStatus == KcdConnStatus.Connecting ||
                         kcd.ConnStatus == KcdConnStatus.Disconnecting);

            // We do not want the KCD to be connected anymore. Ignore the
            // message.
            if (kcd.ConnStatus == KcdConnStatus.Disconnecting) return;

            // The KCD is now connected.
            kcd.ConnStatus = KcdConnStatus.Connected;
            kcd.MinorVersion = notice.MinorVersion;
            kcd.ClearError(true);

            // Notify every workspace that the KCD is connected. Stop if the KCD
            // state changes while notifications are being sent.
            foreach (Workspace kws in kcd.KwsTree.Values)
            {
                if (kcd.ConnStatus != KcdConnStatus.Connected) break;
                kws.Sm.HandleKcdConnStatusChange(KcdConnStatus.Connected, null);
            }
        }

        /// <summary>
        /// Process a KCD disconnection notice.
        /// </summary>
        private static void ProcessKcdDisconnectionNotice(KcdDisconnectionNotice notice)
        {
            Debug.Assert(Wm.KcdTree.ContainsKey(notice.KcdID));
            WmKcd kcd = Wm.KcdTree[notice.KcdID];
            Debug.Assert(kcd.ConnStatus != KcdConnStatus.Disconnected);

            // The KCD died unexpectedly.
            if (kcd.ConnStatus != KcdConnStatus.Disconnecting)
            {
                // Handle the offense.
                if (notice.Ex != null)
                {
                    // Increase the failed connection attempt count if were
                    // connecting.
                    AssignErrorToKcd(kcd, (kcd.ConnStatus == KcdConnStatus.Connecting));
                }
            }

            // The KCD is now disconnected.
            kcd.ConnStatus = KcdConnStatus.Disconnected;

            // Clear the command-reply mappings associated to the KCD.
            kcd.QueryMap.Clear();

            // Notify every workspace that the KCD is disconnected.
            foreach (Workspace kws in kcd.KwsTree.Values)
                kws.Sm.HandleKcdConnStatusChange(KcdConnStatus.Disconnected, notice.Ex);

            // Remove the KCD if we no longer need it.
            Wm.RemoveKcdIfNoRef(kcd);

            // Re-run the state machine, in case we want to reconnect to the KCD
            // or stop.
            RequestRun("KCD disconnected");
        }

        /// <summary>
        /// Process an ANP message received from the KCD.
        /// </summary>
        private static void ProcessKcdAnpMsg(KcdAnpMsg m)
        {
            KLogging.Log("ProcessKcdAnpMsg() called");

            // We're stopping. Bail out.
            if (StopFlag) return;

            // The KCD specified does not exist. Bail out.
            if (!Wm.KcdTree.ContainsKey(m.KcdID)) return;

            // The KCD is not connected. Bail out.
            WmKcd kcd = Wm.KcdTree[m.KcdID];
            if (kcd.ConnStatus != KcdConnStatus.Connected) return;

            // Process the message according to its type.
            try
            {
                if (m.Msg.Type == KAnp.KANP_RES_FAIL && m.Msg.Elements[0].UInt32 == KAnp.KANP_RES_FAIL_BACKEND)
                    throw new Exception("backend error: " + m.Msg.Elements[1].String);
                else if (m.IsReply()) ProcessKcdAnpReply(kcd, m.Msg);
                else if (m.IsEvent()) ProcessKcdAnpEvent(kcd, m.Msg);
                else throw new Exception("received unexpected ANP message type (" + m.Msg.Type + ")");
            }

            catch (Exception ex)
            {
                HandleTroublesomeKcd(kcd, ex);
            }
        }

        /// <summary>
        /// Process an ANP reply received from the KCD.
        /// </summary>
        private static void ProcessKcdAnpReply(WmKcd kcd, AnpMsg msg)
        {
            // We have no knowledge of the query. Ignore the reply.
            if (!kcd.QueryMap.ContainsKey(msg.ID)) return;

            // Retrieve and remove the query from the query map.
            KcdQuery query = kcd.QueryMap[msg.ID];

            // Set the reply in the query.
            query.Res = msg;

            // We don't have non-workspace-related replies yet.
            Debug.Assert(query.Kws != null);

            // Dispatch the message to the workspace.
            query.Kws.Sm.HandleKcdReply(query);
            query.Terminate();
        }

        /// <summary>
        /// Process an ANP event received from the KCD.
        /// </summary>
        private static void ProcessKcdAnpEvent(WmKcd kcd, AnpMsg msg)
        {
            // Get the external workspace ID referred to by the event.
            UInt64 externalID = msg.Elements[0].UInt64;

            // Locate the workspace.
            Workspace kws = kcd.GetWorkspaceByExternalID(externalID);

            // No such workspace, bail out.
            if (kws == null) return;

            // Dispatch the event to its workspace.
            kws.Sm.HandleKcdEvent(msg);
        }

        /// <summary>
        /// Connect a KCD if it is disconnected.
        /// </summary>
        private static void ConnectKcd(WmKcd kcd)
        {
            if (kcd.ConnStatus != KcdConnStatus.Disconnected) return;

            // Clear the current error, but not the failed connection count.
            kcd.ClearError(false);

            // Send a connection request.
            kcd.ConnStatus = KcdConnStatus.Connecting;
            Wm.KcdBroker.RequestKcdConnect(kcd.KcdID);

            // Notify the listeners.
            foreach (Workspace kws in kcd.KwsTree.Values)
                kws.Sm.HandleKcdConnStatusChange(KcdConnStatus.Connecting, null);
        }

        /// <summary>
        /// Disconnect a KCD if it is connecting/connected.
        /// </summary>
        private static void DisconnectKcd(WmKcd kcd, Exception ex)
        {
            if (kcd.ConnStatus == KcdConnStatus.Disconnecting ||
                kcd.ConnStatus == KcdConnStatus.Disconnected) return;

            // Send a disconnection request to the KCD.
            kcd.ConnStatus = KcdConnStatus.Disconnecting;
            Wm.KcdBroker.RequestKcdDisconnect(kcd.KcdID);

            // Notify the listeners.
            foreach (Workspace kws in kcd.KwsTree.Values)
                kws.Sm.HandleKcdConnStatusChange(KcdConnStatus.Disconnecting, ex);
        }

        /// <summary>
        /// Assign an error to a KCD.
        /// </summary>
        private static void AssignErrorToKcd(WmKcd kcd, bool connectFailureFlag)
        {
            kcd.SetError(DateTime.Now, connectFailureFlag);
        }

        /// <summary>
        /// Deliver notifications if delivery is allowed.
        /// </summary>
        private static void TriggerNotif()
        {
            while (m_notifLockCount == 0 && m_notifQueue.Count > 0)
            {
                // While we are firing, we need to lock notifications so that
                // we don't end up with two executions of this method.
                m_notifLockCount++;
                KwsSmNotif n = m_notifQueue.Dequeue();
                Workspace kws = n.Kws;
                
                try
                {
                    if (kws.OnKwsSmNotif != null) kws.OnKwsSmNotif(kws, n);
                }

                catch (Exception ex)
                {
                    // We cannot handle failures in notifications.
                    KBase.HandleException(ex, true);
                }

                m_notifLockCount--;
            }
        }


        /////////////////////////////////////////////
        // Interface methods for internal events. ///
        /////////////////////////////////////////////

        /// <summary>
        /// This method is called by the timer thread to execute the state
        /// machine in a clean context.
        /// </summary>
        private static void HandleTimerWakeUp(Object[] args)
        {
            Run("timer thread");
        }

        /// <summary>
        /// This method is called by the KCD broker when an event interesting
        /// for us has occured. 
        /// </summary>
        public static void HandleKcdBrokerNotification(Object sender, EventArgs args)
        {
            // Set the notification flag, set the next run date to now and run
            // the state machine. By design we're running in a clean context.
            WmKcdState.EventFlag = true;
            m_nextRunDate = DateTime.Now;
            Run("KCD broker");
        }

        /// <summary>
        /// Called when a thread of the KWM has been collected. This is used to
        /// detect when it is safe to quit.
        /// </summary>
        public static void OnThreadCollected(Object sender, EventArgs args)
        {
            if (StopFlag) RequestRun("KWM thread collected");
        }


        /////////////////////////////////////////////////////
        // Interface methods for workspace state machines. //
        /////////////////////////////////////////////////////

        /// <summary>
        /// Called by the workspace manager when the reference count has been
        /// decremented to 0.
        /// </summary>
        public static void HandleUiExit()
        {
            // Run the state machine in case we have to remove some workspaces.
            RequestRun("UI exit");
        }

        /// <summary>
        /// Queue a notification to be delivered.
        /// </summary>
        public static void QueueNotif(KwsSmNotif n)
        {
            m_notifQueue.Enqueue(n);
        }

        /// <summary>
        /// Prevent notifications from being delivered until UnlockNotif() has been
        /// called as many times as LockNotif().
        /// </summary>
        public static void LockNotif()
        {
            m_notifLockCount++;
        }

        /// <summary>
        /// Unlock notifications and deliver them, if possible.
        /// </summary>
        public static void UnlockNotif()
        {
            Debug.Assert(m_notifLockCount > 0);
            m_notifLockCount--;
            TriggerNotif();
        }

        /// <summary>
        /// Handle a workspace that want to be connected.
        /// </summary>
        public static void HandleKwsToConnect(Workspace kws)
        {
            if (kws.InKcdConnectTree()) return;

            // Add the workspace to the KCD connect tree.
            kws.AddToKcdConnectTree();

            // Run our state machine if this is the first connection request.
            if (kws.Kcd.KwsConnectTree.Count == 1) RequestRun("KCD connection request");
        }

        /// <summary>
        /// Handle a workspace that want to be disconnected.
        /// </summary>
        public static void HandleKwsToDisconnect(Workspace kws)
        {
            if (!kws.InKcdConnectTree()) return;

            // Remove the workspace from the KCD connection tree.
            kws.RemoveFromKcdConnectTree();

            // Disconnect the KCD if no workspace want to be connected.
            if (kws.Kcd.KwsConnectTree.Count == 0) DisconnectKcd(kws.Kcd, null);
        }

        /// <summary>
        /// Handle non-recoverable KCD errors (aside of disconnection notices).
        /// This method should be called when a KCD behaves badly.
        /// </summary>
        public static void HandleTroublesomeKcd(WmKcd kcd, Exception ex)
        {
            // Assign the error to the KCD.
            AssignErrorToKcd(kcd, false);

            // Disconnect the KCD.
            DisconnectKcd(kcd, ex);
        }

        /// <summary>
        /// Clear the errors associated to the KCD specified, reset the number
        /// of failed connection attempts to 0 and request a run of the state 
        /// machine, if required. This can be used to force a KCD to reconnect
        /// sooner than usual.
        /// </summary>
        public static void ResetKcdFailureState(WmKcd kcd)
        {
            if (kcd.FailedConnectCount != 0 || kcd.ErrorDate != DateTime.MinValue)
            {
                kcd.ClearError(true);
                RequestRun("cleared KCD error");
            }
        }

        /// <summary>
        /// This method should be called when an ANP event has been processed
        /// to recompute quenching.
        /// </summary>
        public static void HandleKcdEventProcessed()
        {
            // One more event processed.
            WmKcdState.CurrentBatchCount++;

            // Recompute quenching if needed.
            if (!WmKcdState.QuenchFlag) RecomputeKcdQuenching();
        }

        /// <summary>
        /// This method can be called by the state machine methods to request
        /// the state machine to run ASAP.
        /// </summary>
        public static void RequestRun(String reason)
        {
            ScheduleRun(reason, DateTime.MinValue);
        }

        /// <summary>
        /// This method can be called by the state machine methods to request
        /// the state machine to run at Deadline. If Deadline is MinValue, the 
        /// state machine will be run again immediately.
        /// </summary>
        public static void ScheduleRun(String reason, DateTime deadline)
        {
            // Remember the previous next run date.
            DateTime oldNextRunDate = m_nextRunDate;

            // Update the next run date.
            String ls = "WM run scheduled: " + reason + ".";

            if (deadline != DateTime.MinValue)
            {
                ls += " When: " + deadline + ".";
                if (deadline < m_nextRunDate) m_nextRunDate = deadline;
            }

            else
            {
                ls += " When: now.";
                m_nextRunDate = DateTime.MinValue;
            }

            KLogging.Log(ls);

            // If we modified the next run date, notify the timer thread,
            // unless we're running inside the SM, in which case we'll call
            // ScheduleTimerEvent() after the state machine has stabilized.
            if (!m_runningFlag && oldNextRunDate != m_nextRunDate) ScheduleTimerEvent();
        }

        /// <summary>
        /// Request the state machine of every workspace to run ASAP. Also 
        /// request a run of the WM state machine.
        /// </summary>
        public static void RequestAllKwsRun(String reason)
        {
            foreach (Workspace kws in Wm.KwsTree.Values) kws.Sm.NextRunDate = DateTime.MinValue;
            RequestRun("all workspace run: " + reason);
        }


        //////////////////////////////////////
        // Miscellaneous interface methods. //
        //////////////////////////////////////

        /// <summary>
        /// Request the workspace manager to start.
        /// </summary>
        public static void RequestStart()
        {
            if (Wm.MainStatus != WmMainStatus.Stopped) return;

            LockNotif();

            // We're now starting.
            Wm.MainStatus = WmMainStatus.Starting;

            // Start the brokers.
            Wm.KcdBroker.Start();
            Wm.KmodBroker.SetEnabled(true);
            Wm.EAnpBroker.Start();

            // Update the state of the workspaces.
            foreach (Workspace kws in Wm.KwsTree.Values) kws.Sm.UpdateStateOnStartup();

            // Ask a run of all the KWS state machines and of the WM state machine as well.
            m_nextRunDate = DateTime.MaxValue;
            RequestAllKwsRun("WM started");

            // We're now started if we were not requested to stop in
            // the mean time.
            if (Wm.MainStatus == WmMainStatus.Starting)
            {
                Wm.MainStatus = WmMainStatus.Started;
            }

            UnlockNotif();
        }

        /// <summary>
        /// Request the workspace manager to stop.
        /// </summary>
        public static void RequestStop()
        {
            if (StopFlag) return;

            // We're now stopping.
            Wm.MainStatus = WmMainStatus.Stopping;

            // Run our state machine to stop.
            RequestRun("WM stopping");
        }
    }
}