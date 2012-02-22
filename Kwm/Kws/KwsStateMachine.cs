using kcslib;
using kwmlib;
using System;
using System.Diagnostics;
using System.Collections.Generic;

/* Notes about the workspace state machine:
 *
 * The primary role of the state machine is to transition between the various
 * workspace tasks gracefully and to coordinate the delivery of notifications
 * about state changes to the interested parties.
 * 
 * The main levels of functionality managed by the state machine are:
 * 1) The connection to the KCD.
 * 2) The workspace login on the KCD.
 * 3) The execution of the workspace applications.
 * 
 * The complexity of the state machine lies in the delivery of notifications
 * about state changes. Every time such a notification is delivered, the state
 * of the workspace may change again. This situation can potentially lead to a 
 * cascade of notifications, some of them stale. To handle this complexity, the
 * state machine uses a fixed and simple processing pipeline, as follow.
 * 
 * The current workspace task determines the levels of functionality that the
 * state machine needs to attain. When a task switch occurs, the state machine
 * lowers the levels of functionality immediately if required. All interested
 * parties are notified of the state change. Then, the state machine gradually
 * increases the level of functionality, notifying the interested parties along
 * the way. The same processing occurs if an external event causes the levels
 * of functionality to change.
 * 
 * The state machine guarantees that a stable state will be reached eventually.
 * For that purpose, the state machine assumes no knowledge of the behavior of 
 * the external parties. It is built to be resilient to any state change
 * requested by these parties. All it cares about is to manage the workspace
 * tasks and the levels of functionality properly. Consequently, all external
 * parties may safely request state changes at any time. These state change
 * requests will be ignored if they are illegal.
 * 
 * The notifications are delivered when the state machine has finished updating
 * its state. Some of these notifications may be stale, but they are delivered
 * in the order in which they were generated.
 */

namespace kwm
{
    /// <summary>
    /// Represent the run level of a workspace.
    /// </summary>
    public enum KwsRunLevel
    {
        /// <summary>
        /// The workspace isn't ready to work offline.
        /// </summary>
        Stopped,

        /// <summary>
        /// The workspace is ready to work offline.
        /// </summary>
        Offline,

        /// <summary>
        /// The workspace is ready to work online.
        /// </summary>
        Online
    }

    /// <summary>
    /// Current step of the spawn task of the workspace.
    /// </summary>
    public enum KwsSpawnTaskStep
    {
        /// <summary>
        /// Connect to the KCD.
        /// </summary>
        Connect,

        /// <summary>
        /// Login to the KCD.
        /// </summary>
        Login
    }

    /// <summary>
    /// Current step of the rebuilding task of the workspace.
    /// </summary>
    public enum KwsRebuildTaskStep
    {
        /// <summary>
        /// Not yet started.
        /// </summary>
        None,

        /// <summary>
        /// In progress.
        /// </summary>
        InProgress
    }

    /// <summary>
    /// Current step of the server deletion task of the workspace.
    /// </summary>
    public enum KwsDeleteRemotelyStep
    {
        /// <summary>
        /// Waiting for the KCD to be connected and the workspace to be logged 
        /// out.
        /// </summary>
        ConnectedAndLoggedOut,

        /// <summary>
        /// Login to the KCD to delete the workspace.
        /// </summary>
        Login
    }

    /// <summary>
    /// This class represents the state machine of a workspace. The state
    /// machine coordinates the state transitions of the workspace.
    /// </summary>
    public class KwsStateMachine
    {
        /// <summary>
        /// Reference to the workspace.
        /// </summary>
        private Workspace m_kws = null;

        /// <summary>
        /// Reference to the workspace core data.
        /// </summary>
        private KwsCoreData m_cd = null;

        /// <summary>
        /// Reference to the KCD state.
        /// </summary>
        private KwsKcdState m_ks = null;

        /// <summary>
        /// Date at which the state machine needs to run again.
        /// MinValue: the state machine needs to run at the first
        ///           opportunity.
        /// MaxValue: the state machine does not need to run.
        /// Only access this from the context of the state machines.
        /// </summary>
        public DateTime NextRunDate = DateTime.MaxValue;

        /// <summary>
        /// True if a task switch is in progress.
        /// </summary>
        private bool m_taskSwitchFlag = false;

        /// <summary>
        /// Current spawn step, if any.
        /// </summary>
        private KwsSpawnTaskStep m_spawnStep = KwsSpawnTaskStep.Connect;

        /// <summary>
        /// Current rebuilding step, if any.
        /// </summary>
        private KwsRebuildTaskStep m_rebuildStep = KwsRebuildTaskStep.None;

        /// <summary>
        /// Current delete remotely step, if any.
        /// </summary>
        private KwsDeleteRemotelyStep m_deleteRemotelyStep = KwsDeleteRemotelyStep.ConnectedAndLoggedOut;

        public void Relink(Workspace kws)
        {
            m_kws = kws;
            m_cd = kws.Cd;
            m_ks = m_cd.KcdState;
        }

        /// <summary>
        /// Validate our internal state invariants.
        /// </summary>
        private void CheckInvariants()
        {
            Debug.Assert(m_cd.UserTask == KwsTask.Stop ||
                         m_cd.UserTask == KwsTask.WorkOffline ||
                         m_cd.UserTask == KwsTask.WorkOnline);

            // Conditional xor check for the deletion tree.
            Debug.Assert((m_cd.CurrentTask != KwsTask.DeleteLocally) ^ (m_kws.InKwsRemoveTree()));

            // The applications shouldn't be running if we don't want them to run.
            Debug.Assert(WantAppRunning() ||
                         m_cd.AppStatus == KwsAppStatus.Stopping ||
                         m_cd.AppStatus == KwsAppStatus.Stopped);

            // We shouldn't be in the KCD connect tree if we don't want to be connected.
            Debug.Assert(WantKcdConnected() || !m_kws.InKcdConnectTree());

            // A logout request should have been sent if we don't want to be logged in.
            Debug.Assert(WantLogin() ||
                         m_ks.LoginStatus == KwsLoginStatus.LoggingOut ||
                         m_ks.LoginStatus == KwsLoginStatus.LoggedOut);

            // We should be logged out if we are not connected.
            Debug.Assert(m_kws.Kcd.ConnStatus == KcdConnStatus.Connected ||
                         m_ks.LoginStatus == KwsLoginStatus.LoggedOut);
        }

        /// <summary>
        /// Return true if the state machine wants to run now.
        /// </summary>
        private bool WantToRunNow()
        {
            // Fast track.
            if (NextRunDate == DateTime.MinValue) return true;

            // System call path.
            return (NextRunDate <= DateTime.Now);
        }

        /// <summary>
        /// Run once through the bowels of the state machine.
        /// </summary>
        private void RunPass()
        {
            // Reset the next run date to the maximum. During the processing of this
            // method, the next run date will lower itself as needed.
            NextRunDate = DateTime.MaxValue;

            CheckInvariants();

            // Handle task-specific actions.
            ProcessKwsRebuildIfNeeded();

            // Start the applications if required.
            StartAppIfNeeded();

            // Connect to the KCD if required.
            ConnectToKcdIfNeeded();

            // Send a login request if required.
            LoginIfNeeded();

            // Dispatch the unprocessed KCD ANP events if required.
            DispatchUnprocessedKcdEvents();

            CheckInvariants();
        }

        /// <summary>
        /// Return true if the applications should be running.
        /// </summary>
        private bool WantAppRunning()
        {
            return (m_cd.CurrentTask == KwsTask.WorkOffline || m_cd.CurrentTask == KwsTask.WorkOnline);
        }

        /// <summary>
        /// Return true if the KCD of the workspace should be connected.
        /// </summary>
        private bool WantKcdConnected()
        {
            return (m_cd.CurrentTask == KwsTask.WorkOnline ||
                    m_cd.CurrentTask == KwsTask.Spawn ||
                    m_cd.CurrentTask == KwsTask.DeleteRemotely);
        }

        /// <summary>
        /// Return true if the workspace should be logged in.
        /// </summary>
        private bool WantLogin()
        {
            return (m_cd.CurrentTask == KwsTask.WorkOnline ||
                    m_cd.CurrentTask == KwsTask.Spawn && m_spawnStep >= KwsSpawnTaskStep.Login ||
                    m_cd.CurrentTask == KwsTask.DeleteRemotely && m_deleteRemotelyStep >= KwsDeleteRemotelyStep.Login);
        }

        /// <summary>
        /// Start the applications if required.
        /// </summary>
        private void StartAppIfNeeded()
        {
            if (WantAppRunning()) StartApp();
        }

        /// <summary>
        /// Stop the applications if required.
        /// </summary>
        private void StopAppIfNeeded(Exception ex)
        {
            if (!WantAppRunning()) StopApp(ex);
        }

        /// <summary>
        /// Connect to the KCD if required.
        /// </summary>
        private void ConnectToKcdIfNeeded()
        {
            if (WantKcdConnected() && !m_kws.InKcdConnectTree()) WmSm.HandleKwsToConnect(m_kws);
        }

        /// <summary>
        /// Disconnect from the KCD if required.
        /// </summary>
        private void DisconnectFromKcdIfNeeded()
        {
            if (!WantKcdConnected() && m_kws.InKcdConnectTree()) WmSm.HandleKwsToDisconnect(m_kws);
        }

        /// <summary>
        /// Login if required.
        /// </summary>
        private void LoginIfNeeded()
        {
            if (WantLogin() &&
                m_kws.Kcd.ConnStatus == KcdConnStatus.Connected &&
                m_ks.LoginStatus == KwsLoginStatus.LoggedOut)
            {
                m_ks.LoginStatus = KwsLoginStatus.LoggingIn;
                m_kws.KcdLoginHandler.PerformLogin();
            }
        }

        /// <summary>
        /// Logout if required.
        /// </summary>
        private void LogoutIfNeeded()
        {
            if (!WantLogin() &&
                m_ks.LoginStatus != KwsLoginStatus.LoggedOut &&
                m_ks.LoginStatus != KwsLoginStatus.LoggingOut)
            {
                Debug.Assert(m_kws.Kcd.ConnStatus == KcdConnStatus.Connected);
                m_ks.LoginStatus = KwsLoginStatus.LoggingOut;
                m_kws.KcdLoginHandler.PerformLogout();
            }
        }

        /// <summary>
        /// Interrupt any rebuild in progress if needed.
        /// </summary>
        private void StopRebuildIfNeeded()
        {
            m_rebuildStep = KwsRebuildTaskStep.None;
        }

        /// <summary>
        /// Switch the current task to the task specified. Don't call this 
        /// outside RequestSwitchTask() unless you know what you are doing.
        /// The state machine will run ASAP to handle the new state.
        /// </summary>
        private void SwitchTask(KwsTask task, Exception ex)
        {
            // The order of the calls is important here.
            WmSm.LockNotif();
            Debug.Assert(m_taskSwitchFlag == false);
            m_taskSwitchFlag = true;
            m_cd.CurrentTask = task;
            StopAppIfNeeded(ex);
            DisconnectFromKcdIfNeeded();
            LogoutIfNeeded();
            StopRebuildIfNeeded();
            UpdateKcdEventUpToDateState();
            m_kws.OnStateChange(WmStateChange.Transient);
            RequestRun("task switch to " + task);
            m_taskSwitchFlag = false;
            WmSm.QueueNotif(new KwsSmNotifTaskSwitch(m_kws, task, ex));
            WmSm.UnlockNotif();
        }

        /// <summary>
        /// Switch to the user task if possible.
        /// </summary>
        private void SwitchToUserTask()
        {
            RequestTaskSwitch(m_cd.UserTask);
        }

        /// <summary>
        /// Process workspace rebuild if required.
        /// </summary>
        private void ProcessKwsRebuildIfNeeded()
        {
            Debug.Assert(m_rebuildStep == KwsRebuildTaskStep.None);

            if (m_cd.CurrentTask != KwsTask.Rebuild) return;

            // Sanity check.
            if (m_cd.MainStatus != KwsMainStatus.RebuildRequired)
            {
                KLogging.Log("cannot execute rebuild task, " + KwmStrings.Kws + " status is not RebuildRequired");
                RequestTaskSwitch(KwsTask.Stop);
                return;
            }

            // We cannot rebuild until the applications are stopped and we're
            // logged out.
            if (m_cd.AppStatus != KwsAppStatus.Stopped || m_ks.LoginStatus != KwsLoginStatus.LoggedOut)
                return;

            // Protect against spurious state changes.
            m_rebuildStep = KwsRebuildTaskStep.InProgress;

            try
            {
                // Ask the workspace to prepare for rebuild.
                m_kws.PrepareToRebuild();
                if (m_rebuildStep != KwsRebuildTaskStep.InProgress) return;

                // Tell the applications to prepare for rebuild.
                foreach (KwsApp app in m_kws.AppTree.Values)
                {
                    app.PrepareToRebuild();
                    if (m_rebuildStep != KwsRebuildTaskStep.InProgress) return;
                }
            }

            catch (Exception ex)
            {
                HandleMiscFailure(ex);
                return;
            }

            // We have "rebuilt" the workspace. Update the state.
            m_rebuildStep = KwsRebuildTaskStep.None;
            m_cd.MainStatus = KwsMainStatus.Good;
            m_cd.RebuildFlags = 0;
            m_cd.Uuid = Wm.MakeUuid();
            SetLoginType(KwsLoginType.All);
            m_kws.OnStateChange(WmStateChange.Permanent);

            // Switch to the user task.
            SwitchToUserTask();
        }

        /// <summary>
        /// Request the WM to clear the errors of our KCD, if any.
        /// </summary>
        private void ResetKcdFailureState()
        {
            WmSm.ResetKcdFailureState(m_kws.Kcd);
        }

        /// <summary>
        /// Blame the KCD of this workspace for the failure specified.
        /// </summary>
        private void BlameKcd(Exception ex)
        {
            WmSm.HandleTroublesomeKcd(m_kws.Kcd, ex);
        }

        /// <summary>
        /// Dispatch the unprocessed KCD ANP events.
        /// </summary>
        private void DispatchUnprocessedKcdEvents()
        {
            while (m_ks.NbUnprocessedEvent > 0 &&
                   !WmKcdState.QuenchFlag &&
                   m_kws.IsOfflineCapable())
            {
                AnpMsg msg = m_kws.GetFirstUnprocessedEventInDb();
                if (msg == null)
                    throw new Exception(KwmStrings.Kws + " thinks it has unprocessed events, but DB says there aren't.");
                DispatchKcdEvent(msg);
            }
        }

        /// <summary>
        /// Dispatch an KCD event and update the state as needed.
        /// </summary>
        private void DispatchKcdEvent(AnpMsg msg)
        {
            // Dispatch the event to the appropriate handler.
            KwsAnpEventStatus newStatus = DispatchAnpEventToHandler(msg);

            // For quenching purposes we assume the event was processed.
            WmSm.HandleKcdEventProcessed();

            // If the ANP event has been processed, update its entry in the 
            // database and the catch up state as required.
            if (newStatus == KwsAnpEventStatus.Processed)
            {
                if (msg.ID > 0)
                {
                    Debug.Assert(m_ks.NbUnprocessedEvent > 0);
                    m_kws.UpdateKAnpEventStatusInDb(msg.ID, KwsAnpEventStatus.Processed);
                    m_ks.NbUnprocessedEvent--;
                    UpdateKcdEventUpToDateState();
                    m_kws.OnStateChange(WmStateChange.Permanent);
                }
            }
        }

        /// <summary>
        /// Dispatch an ANP event to the appropriate handler.
        /// </summary>
        private KwsAnpEventStatus DispatchAnpEventToHandler(AnpMsg msg)
        {
            // If this event version is not supported, disable the workspace.
            if (msg.Minor > KAnp.Minor)
            {
                RequestTaskSwitch(KwsTask.Stop, new EAnpExUpgradeKwm());
                return KwsAnpEventStatus.Unprocessed;
            }

            // Dispatch to the appropriate handler.
            try
            {
                UInt32 ns = KAnp.GetNsFromType(msg.Type);
                KwsAnpEventStatus status = KwsAnpEventStatus.Unprocessed;

                // Non-application-specific event.
                if (ns == KAnp.KANP_NS_KWS)
                {
                    status = m_kws.KcdEventHandler.HandleAnpEvent(msg);
                }

                // Application-specific event.
                else
                {
                    // Trivially process whiteboard events.
                    if (ns == KAnp.KANP_NS_WB) return KwsAnpEventStatus.Processed;

                    // Locate the application.
                    KwsApp app = m_kws.GetApp(ns);
                    if (app == null) throw new Exception("unknown application of type " + ns);

                    // Dispatch.
                    status = app.HandleAnpEvent(msg);
                }

                // Throw an exception if we cannot process an event that we
                // should have been able to process.
                if (status == KwsAnpEventStatus.Unprocessed && m_kws.IsOfflineCapable())
                    throw new Exception("failed to process KCD event");

                return status;
            }

            catch (Exception ex)
            {
                HandleMiscFailure(ex);
                return KwsAnpEventStatus.Unprocessed;
            }
        }

        /// <summary>
        /// Compute the value of KcdEventUpToDateFlag.
        /// </summary>
        private bool AreKcdEventUpToDate()
        {
            if (m_cd.CurrentTask == KwsTask.WorkOffline)
                return (m_ks.NbUnprocessedEvent == 0);

            if (m_cd.CurrentTask == KwsTask.WorkOnline)
                return (m_ks.NbUnprocessedEvent == 0 &&
                        m_ks.LoginStatus == KwsLoginStatus.LoggedIn);

            return false;
        }

        /// <summary>
        /// Update KcdEventUpToDateFlag, EAnpFreshnessID and handle notifications.
        /// </summary>
        private void UpdateKcdEventUpToDateState()
        {
            bool newValue = AreKcdEventUpToDate();
            if (m_ks.KcdEventUpToDateFlag == newValue) return;
            m_ks.KcdEventUpToDateFlag = newValue;
            if (newValue)
            {
                WmSm.QueueNotif(new KwsSmNotifKcdEventUpToDate(m_kws));
            }
        }

        /// <summary>
        /// Start the applications if they are stopped.
        /// </summary>
        private void StartApp()
        {
            if (m_cd.AppStatus != KwsAppStatus.Stopped) return;

            m_cd.AppStatus = KwsAppStatus.Starting;

            try
            {
                // Prepare the workspace to work.
                m_kws.PrepareToWork();
                if (m_cd.AppStatus != KwsAppStatus.Starting) return;

                // Prepare the applications to work.
                foreach (KwsApp app in m_kws.AppTree.Values)
                {
                    app.PrepareToWork();
                    if (m_cd.AppStatus != KwsAppStatus.Starting) return;
                }

                // Ask the applications to start.
                foreach (KwsApp app in m_kws.AppTree.Values)
                {
                    Debug.Assert(app.AppStatus == KwsAppStatus.Stopped);
                    app.AppStatus = KwsAppStatus.Starting;
                    app.Start();
                    if (m_cd.AppStatus != KwsAppStatus.Starting) return;
                }

                // Required if there are no applications.
                OnAppStarted();
            }

            catch (Exception ex)
            {
                HandleMiscFailure(ex);
            }
        }

        /// <summary>
        /// Stop the applications if they are starting / started.
        /// </summary>
        private void StopApp(Exception ex)
        {
            if (m_cd.AppStatus == KwsAppStatus.Stopped || m_cd.AppStatus == KwsAppStatus.Stopping) return;

            try
            {
                m_cd.AppStatus = KwsAppStatus.Stopping;

                foreach (KwsApp app in m_kws.AppTree.Values)
                {
                    if (app.AppStatus == KwsAppStatus.Started || app.AppStatus == KwsAppStatus.Starting)
                        app.AppStatus = KwsAppStatus.Stopping;
                    app.Stop(ex);
                }

                // Required if there are no applications.
                OnAppStopped();
            }

            // We cannot handle applications that won't stop.
            catch (Exception ex2)
            {
                KBase.HandleException(ex2, true);
            }
        }


        ///////////////////////////////////////////
        // Interface methods for state machines. //
        ///////////////////////////////////////////

        /// <summary>
        /// This method is called by the WM to stop the workspace. This method
        /// must return true when the workspace has stopped.
        /// </summary>
        public bool TryStop()
        {
            if (m_cd.CurrentTask != KwsTask.Stop && m_cd.CurrentTask != KwsTask.DeleteLocally) RequestTaskSwitch(KwsTask.Stop);
            if (m_cd.CurrentTask != KwsTask.Stop) return false;
            if (m_cd.AppStatus != KwsAppStatus.Stopped) return false;
            if (m_ks.LoginStatus != KwsLoginStatus.LoggedOut) return false;
            return true;
        }

        /// <summary>
        /// Update the state of the workspace when the workspace manager is
        /// starting.
        /// </summary>
        public void UpdateStateOnStartup()
        {
            // We should be stopped at this point.
            Debug.Assert(m_cd.CurrentTask == KwsTask.Stop);
            CheckInvariants();

            // The workspace shouldn't exist anymore. Request its deletion.
            if (m_cd.MainStatus == KwsMainStatus.NotYetSpawned ||
                m_cd.MainStatus == KwsMainStatus.OnTheWayOut)
            {
                m_kws.Sm.RequestTaskSwitch(KwsTask.DeleteLocally);
            }

            // A rebuild is required. Keep the workspace stopped.
            else if (m_cd.MainStatus == KwsMainStatus.RebuildRequired)
            {
                // Void.
            }

            // The workspace is ready to work. Switch to the user task, if needed.
            else
            {
                SwitchToUserTask();
            }

            CheckInvariants();
        }

        /// <summary>
        /// This method can be called by the state machine methods and its helpers 
        /// to request the state machine to run ASAP.
        /// </summary>
        public void RequestRun(String reason)
        {
            ScheduleRun(reason, DateTime.MinValue);
        }

        /// <summary>
        /// This method can be called by the state machine methods to request
        /// the state machine to run at Deadline. If Deadline is MinValue, the 
        /// state machine will be run again immediately.
        /// </summary>
        public void ScheduleRun(String reason, DateTime deadline)
        {
            if (deadline < NextRunDate)
            {
                NextRunDate = deadline;
                WmSm.ScheduleRun(KwmStrings.Kws + m_kws.InternalID + ": " + reason, deadline);
            }
        }

        /// <summary>
        /// Run the workspace state machine.
        /// 
        /// Important: this method should only be called by the workspace manager
        ///            state machine.
        /// </summary>
        public void Run()
        {
            // Loop until our state stabilize.
            while (WantToRunNow()) RunPass();
        }

        /// <summary>
        /// This method returns true if the workspace can be removed safely.
        /// </summary>
        public bool ReadyToRemove()
        {
            return (m_cd.CurrentTask == KwsTask.DeleteLocally &&
                    WmUi.UiEntryCount == 0 &&
                    m_cd.AppStatus == KwsAppStatus.Stopped &&
                    m_ks.LoginStatus == KwsLoginStatus.LoggedOut);
        }

        /// <summary>
        /// Called when the KCD connection status has changed. This method is
        /// only called by the WM state machine.
        /// </summary>
        public void HandleKcdConnStatusChange(KcdConnStatus status, Exception ex)
        {
            // Update our login state.
            if (status == KcdConnStatus.Disconnecting || status == KcdConnStatus.Disconnected)
                UpdateStateOnLogout(ex);

            // Notify the listeners.
            WmSm.QueueNotif(new KwsSmNotifKcdConn(m_kws, status, ex));

            // Let the state machine sort it out.
            RequestRun("KCD connection status change");
        }

        /// <summary>
        /// Called when the workspace becomes logged in.
        /// </summary>
        public void HandleLoginSuccess()
        {
            WmSm.LockNotif();

            Debug.Assert(m_ks.LoginStatus == KwsLoginStatus.LoggingIn);

            // We're now logged in.
            m_ks.LoginStatus = KwsLoginStatus.LoggedIn;

            // Update the event up to date state.
            UpdateKcdEventUpToDateState();

            // Notify the listeners.
            WmSm.QueueNotif(new KwsSmNotifKcdLogin(m_kws, m_ks.LoginStatus, null));

            // Let the state machine sort it out.
            RequestRun("Workspace login success");

            WmSm.UnlockNotif();
        }

        /// <summary>
        /// Called when the login fails for some reason.
        /// </summary>
        public void HandleLoginFailure(Exception ex)
        {
            WmSm.LockNotif();

            Debug.Assert(m_ks.LoginStatus == KwsLoginStatus.LoggingIn);

            // Update our login state.
            UpdateStateOnLogout(ex);

            // The events are out of sync.
            if (m_ks.LoginResult == KwsLoginResult.OOS)
            {
                // Request a rebuild. We have to delete both the cached events and
                // the user data. This is nasty.
                if (m_cd.MainStatus == KwsMainStatus.Good || m_cd.MainStatus == KwsMainStatus.RebuildRequired)
                {
                    m_cd.MainStatus = KwsMainStatus.RebuildRequired;
                    m_cd.RebuildFlags = KwsRebuildFlag.FlushKcdData | KwsRebuildFlag.FlushLocalData;
                }
            }

            // Make the workspace work offline if required. The core operations
            // may have already marked the workspace for removal.
            if (m_cd.CurrentTask == KwsTask.WorkOnline)
            {
                SetUserTask(KwsTask.WorkOffline);
                RequestTaskSwitch(KwsTask.WorkOffline, ex);
            }

            WmSm.UnlockNotif();
        }

        /// <summary>
        /// Called when the workspace logs out normally.
        /// </summary>
        public void HandleNormalLogout()
        {
            WmSm.LockNotif();

            Debug.Assert(m_ks.LoginStatus == KwsLoginStatus.LoggingOut);

            // Update our login state.
            UpdateStateOnLogout(null);

            WmSm.UnlockNotif();
        }

        /// <summary>
        /// Called when the workspace logs out normally, the login fails or
        /// the connection to the KCD is lost.
        /// </summary>
        private void UpdateStateOnLogout(Exception ex)
        {
            if (m_ks.LoginStatus == KwsLoginStatus.LoggedOut) return;

            // Set the last exception.
            m_kws.Cd.LastException = ex;
            m_kws.OnStateChange(WmStateChange.Transient);

            // Update the login status.
            m_kws.KcdLoginHandler.ResetOnLogout();

            // Cancel all the pending KCD queries that depend on the login 
            // state.
            m_kws.Kcd.CancelKwsKcdQuery(m_kws);

            // Update the event up to date state.
            UpdateKcdEventUpToDateState();

            // Notify the listeners.
            WmSm.QueueNotif(new KwsSmNotifKcdLogin(m_kws, m_ks.LoginStatus, ex));
        }

        /// <summary>
        /// Handle an ANP reply received from the KCD.
        /// </summary>
        public void HandleKcdReply(KcdQuery query)
        {
            try
            {
                // Call the callback.
                query.Callback(query);
            }

            catch (Exception ex)
            {
                // The event handler throws if the KCD sends us garbage.
                BlameKcd(ex);
            }
        }

        /// <summary>
        /// Handle an ANP event received from the KCD.
        /// </summary>
        public void HandleKcdEvent(AnpMsg msg)
        {
            KLogging.Log("HandleAnpEvent() in kws " + m_kws.InternalID + ", status " + m_cd.MainStatus);

            // This is a permanent event.
            if (msg.ID > 0)
            {
                // Logic problem detected.
                if (msg.ID < m_ks.LastReceivedEventId)
                {
                    BlameKcd(new Exception("received ANP event with bogus ID"));
                    return;
                }

                // Store the event in the database. Mark it as unprocessed.
                m_kws.StoreKAnpEventInDb(msg, KwsAnpEventStatus.Unprocessed);

                // Update the information about the events.
                m_ks.NbUnprocessedEvent++;
                m_ks.LastReceivedEventId = msg.ID;
                m_kws.OnStateChange(WmStateChange.Permanent);
            }

            // If this is a transient event or the only unprocessed event, 
            // dispatch it right away if possible. This is done so that single 
            // incoming events are processed very quickly instead of waiting 
            // for a future workspace state machine run.
            if (msg.ID == 0 ||
                (m_ks.NbUnprocessedEvent == 1 &&
                 !WmKcdState.QuenchFlag &&
                 m_kws.IsOfflineCapable())) DispatchKcdEvent(msg);
        }


        /////////////////////////////////////////////
        // Interface methods for external parties. //
        /////////////////////////////////////////////

        /// <summary>
        /// This method must be called by each application when it has started.
        /// </summary>
        public void OnAppStarted()
        {
            // The applications are no longer starting.
            if (m_cd.AppStatus != KwsAppStatus.Starting) return;

            // Not all applications have started.
            foreach (KwsApp app in m_kws.AppTree.Values)
                if (app.AppStatus != KwsAppStatus.Started) return;

            WmSm.LockNotif();

            // All applications are started.
            m_cd.AppStatus = KwsAppStatus.Started;

            // Notify the listeners.
            WmSm.QueueNotif(new KwsSmNotifApp(m_kws, m_cd.AppStatus));

            // Let the state machine sort it out.
            RequestRun("Applications started");

            WmSm.UnlockNotif();
        }

        /// <summary>
        /// This method must be called by each application when it has stopped.
        /// </summary>
        public void OnAppStopped()
        {
            // The applications are no longer stopping.
            if (m_cd.AppStatus != KwsAppStatus.Stopping) return;

            // Not all applications have stopped.
            foreach (KwsApp app in m_kws.AppTree.Values)
                if (app.AppStatus != KwsAppStatus.Stopped) return;

            WmSm.LockNotif();

            // All applications are stopped.
            m_cd.AppStatus = KwsAppStatus.Stopped;

            // Notify the listeners.
            WmSm.QueueNotif(new KwsSmNotifApp(m_kws, m_cd.AppStatus));

            // Let the state machine sort it out.
            RequestRun("Applications stopped");

            WmSm.UnlockNotif();
        }

        /// <summary>
        /// This method should be called when an unexpected failure occurs
        /// in the workspace.
        /// </summary>
        public void HandleMiscFailure(Exception ex)
        {
            WmSm.LockNotif();

            KLogging.LogException(ex);

            // We cannot handle failures during task switches. We need the
            // task switches to succeed to recover from failures.
            if (m_taskSwitchFlag) KBase.HandleException(ex, true);

            // Increase the severity of the rebuild required if possible.
            if (m_cd.CurrentTask == KwsTask.Rebuild)
                WorsenRebuild(KwsRebuildFlag.FlushKcdData | KwsRebuildFlag.FlushLocalData);

            // Stop the workspace if required.
            SetUserTask(KwsTask.Stop);
            RequestTaskSwitch(KwsTask.Stop, ex);

            // Let the state machine sort it out.
            RequestRun("application failure");

            WmSm.UnlockNotif();
        }

        /// <summary>
        /// Set the next spawn step, if possible.
        /// </summary>
        public void SetSpawnStep(KwsSpawnTaskStep step)
        {
            if (m_spawnStep >= step) return;
            m_spawnStep = step;
            ResetKcdFailureState();
            RequestRun("set spawn step to " + step);
        }

        /// <summary>
        /// Return the current delete on server step.
        /// </summary>
        public KwsDeleteRemotelyStep GetDeleteRemotelyStep()
        {
            return m_deleteRemotelyStep;
        }

        /// <summary>
        /// Set the next delete on server step, if possible.
        /// </summary>
        public void SetDeleteRemotelyStep(KwsDeleteRemotelyStep step)
        {
            if (m_deleteRemotelyStep >= step) return;
            m_deleteRemotelyStep = step;
            RequestRun("set server delete step to " + step);
        }

        /// <summary>
        /// Set the login type. This method has no effect is the login process
        /// is under way.
        /// </summary>
        public void SetLoginType(KwsLoginType type)
        {
            if (m_ks.LoginStatus == KwsLoginStatus.LoggingIn) return;
            m_kws.KcdLoginHandler.SetLoginType(type);
        }

        /// <summary>
        /// Increase the severity of the rebuild with the parameters provided. 
        /// </summary>
        public void WorsenRebuild(KwsRebuildFlag flags)
        {
            m_cd.RebuildFlags |= flags;
            m_kws.OnStateChange(WmStateChange.Permanent);
        }

        /// <summary>
        /// Set the task the user would like to run when the WM starts. The
        /// current task is unaffected.
        /// </summary>
        public void SetUserTask(KwsTask userTask)
        {
            if ((userTask == KwsTask.Stop || userTask == KwsTask.WorkOffline || userTask == KwsTask.WorkOnline) &&
                m_cd.UserTask != userTask)
            {
                m_cd.UserTask = userTask;
                m_kws.OnStateChange(WmStateChange.Permanent);
            }
        }

        /// <summary>
        /// Return the current run level of the workspace.
        /// </summary>
        public KwsRunLevel GetRunLevel()
        {
            if ((m_cd.CurrentTask != KwsTask.WorkOffline && m_cd.CurrentTask != KwsTask.WorkOnline) ||
                m_cd.AppStatus != KwsAppStatus.Started)
                return KwsRunLevel.Stopped;

            if (m_cd.CurrentTask == KwsTask.WorkOnline && m_ks.LoginStatus == KwsLoginStatus.LoggedIn)
                return KwsRunLevel.Online;

            return KwsRunLevel.Offline;
        }

        /// <summary>
        /// Return true if the workspace can be requested to stop.
        /// </summary>
        public bool CanStop()
        {
            return (m_cd.CurrentTask != KwsTask.DeleteLocally && m_cd.CurrentTask != KwsTask.Stop);
        }

        /// <summary>
        /// Return true if the workspace can be requested to be spawn.
        /// </summary>
        public bool CanSpawn()
        {
            return (m_cd.MainStatus == KwsMainStatus.NotYetSpawned && m_cd.CurrentTask == KwsTask.Stop);
        }

        /// <summary>
        /// Return true if the workspace can be requested to be rebuilt.
        /// </summary>
        public bool CanRebuild()
        {
            return (m_cd.MainStatus != KwsMainStatus.NotYetSpawned &&
                    m_cd.MainStatus != KwsMainStatus.OnTheWayOut &&
                    m_cd.CurrentTask != KwsTask.Rebuild &&
                    m_cd.CurrentTask != KwsTask.DeleteRemotely);
        }

        /// <summary>
        /// Return true if the workspace can be requested to work offline.
        /// </summary>
        public bool CanWorkOffline()
        {
            return (m_cd.MainStatus == KwsMainStatus.Good &&
                    m_cd.CurrentTask != KwsTask.WorkOffline &&
                    m_cd.CurrentTask != KwsTask.DeleteRemotely);
        }

        /// <summary>
        /// Return true if the workspace can be requested to work online.
        /// </summary>
        public bool CanWorkOnline()
        {
            return (m_cd.MainStatus == KwsMainStatus.Good &&
                    m_cd.CurrentTask != KwsTask.DeleteRemotely &&
                    (m_cd.CurrentTask != KwsTask.WorkOnline ||
                     m_kws.Kcd.ConnStatus == KcdConnStatus.Disconnecting ||
                     m_kws.Kcd.ConnStatus == KcdConnStatus.Disconnected));
        }

        /// <summary>
        /// Return true if the workspace can be requested to be deleted locally.
        /// </summary>
        public bool CanDeleteLocally()
        {
            return (m_cd.CurrentTask != KwsTask.DeleteLocally);
        }

        /// <summary>
        /// Return true if the workspace can be requested to be deleted on the
        /// server.
        /// </summary>
        public bool CanDeleteRemotely()
        {
            return (m_cd.MainStatus != KwsMainStatus.OnTheWayOut &&
                    m_cd.CurrentTask != KwsTask.DeleteRemotely &&
                    m_cd.CurrentTask != KwsTask.DeleteLocally);
        }

        /// <summary>
        /// Return true if the workspace credentials can be exported.
        /// </summary>
        public bool CanExport()
        {
            return (m_cd.CurrentTask != KwsTask.DeleteRemotely &&
                    (m_cd.MainStatus == KwsMainStatus.Good || m_cd.MainStatus == KwsMainStatus.RebuildRequired));
        }

        /// <summary>
        /// Request a switch to the task specified, if possible.
        /// </summary>
        public void RequestTaskSwitch(KwsTask task)
        {
            RequestTaskSwitch(task, null);
        }

        /// <summary>
        /// Request a switch to the task specified, if possible. 'Ex' is 
        /// non-null if the task switch is occurring because an error occurred.
        /// </summary>
        public void RequestTaskSwitch(KwsTask task, Exception ex)
        {
            WmSm.LockNotif();

            try
            {
                // Validate.
                if (m_taskSwitchFlag ||
                    task == KwsTask.Stop && !CanStop() ||
                    task == KwsTask.Spawn && !CanSpawn() ||
                    task == KwsTask.Rebuild && !CanRebuild() ||
                    task == KwsTask.WorkOffline && !CanWorkOffline() ||
                    task == KwsTask.WorkOnline && !CanWorkOnline() ||
                    task == KwsTask.DeleteLocally && !CanDeleteLocally() ||
                    task == KwsTask.DeleteRemotely && !CanDeleteRemotely())
                {
                    KLogging.Log("Request to switch to task " + task + " ignored.");
                    return;
                }

                KLogging.Log("Switching to task " + task + ".");

                // Update some state prior to the task switch.
                if (task == KwsTask.Rebuild)
                {
                    m_cd.MainStatus = KwsMainStatus.RebuildRequired;
                    m_rebuildStep = KwsRebuildTaskStep.None;
                }

                else if (task == KwsTask.WorkOnline)
                {
                    ResetKcdFailureState();
                }

                else if (task == KwsTask.DeleteLocally)
                {
                    m_cd.MainStatus = KwsMainStatus.OnTheWayOut;
                    m_kws.AddToKwsRemoveTree();
                    m_kws.OnStateChange(WmStateChange.Permanent);
                }

                else if (task == KwsTask.DeleteRemotely)
                {
                    ResetKcdFailureState();
                    m_deleteRemotelyStep = KwsDeleteRemotelyStep.ConnectedAndLoggedOut;
                }

                if (task == KwsTask.Spawn ||
                    task == KwsTask.Rebuild ||
                    task == KwsTask.WorkOffline ||
                    task == KwsTask.WorkOnline)
                {
                    m_cd.LastException = null;
                    m_kws.OnStateChange(WmStateChange.Permanent);
                }

                // Perform the task switch, if required.
                if (task != m_cd.CurrentTask) SwitchTask(task, ex);
            }

            catch (Exception ex2)
            {
                KBase.HandleException(ex2, true);
            }

            finally
            {
                WmSm.UnlockNotif();
            }
        }
    }
}
