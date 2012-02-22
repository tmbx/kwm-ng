using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace kwm
{
    /// <summary>
    /// Connection status of a KCD as seen by the worker thread.
    /// </summary>
    public enum KcdThreadConnStatus
    {
        /// <summary>
        /// Scheduled for connection,
        /// </summary>
        Scheduled,

        /// <summary>
        /// TCP connection being established.
        /// </summary>
        Connecting,

        /// <summary>
        /// Waiting for KCD role negociation reply.
        /// </summary>
        RoleReply,

        /// <summary>
        /// Connected in workspace mode.
        /// </summary>
        Connected,

        /// <summary>
        /// Connection lost.
        /// </summary>
        Disconnected
    };

    /// <summary>
    /// This class represents a KCD host managed by the KCD thread.
    /// </summary>
    public class KcdThreadHost
    {
        /// <summary>
        /// KCD identifier.
        /// </summary>
        public KcdIdentifier KcdID;

        /// <summary>
        /// Connection status.
        /// </summary>
        public KcdThreadConnStatus ConnStatus = KcdThreadConnStatus.Scheduled;

        /// <summary>
        /// Represent the tunnel made with ktlstunnel.
        /// </summary>
        public AnpTunnel Tunnel;

        /// <summary>
        /// Queue of messages to send to the KCD.
        /// </summary>
        public Queue<AnpMsg> SendQueue = new Queue<AnpMsg>();

        /// <summary>
        /// Exception caught when the connection fails.
        /// </summary>
        public Exception Ex;

        /// <summary>
        /// Transport object of the ANP tunnel. This is null if the tunnel is
        /// not connected.
        /// </summary>
        public AnpTransport Transport { get { return Tunnel.GetTransport(); } }

        public KcdThreadHost(KcdIdentifier kcdID)
        {
            KcdID = kcdID;
            Tunnel = new AnpTunnel(KcdID.Host, (int)KcdID.Port);
        }

        /// <summary>
        /// This method verifies that no message has been received when it is 
        /// called.
        /// </summary>
        public void CheckNoMessageReceivedInvariant()
        {
            Debug.Assert(Tunnel == null || Transport == null || !Tunnel.HasReceivedMessage());
        }

        /// <summary>
        /// Send an ANP message. Side effect free.
        /// </summary>
        public void SafeSend(AnpMsg msg)
        {
            Transport.SendMsg(msg);
        }

        /// <summary>
        /// Send the next message queued in SendQueue if possible.
        /// </summary>
        public void SendNextQueuedMsgIfNeeded()
        {
            if (!Tunnel.IsSendingMessage() && SendQueue.Count != 0) SafeSend(SendQueue.Dequeue());
        }

        /// <summary>
        /// Send the select role command message.
        /// </summary>
        public void SendSelectRoleMsg()
        {
            AnpMsg msg = new AnpMsg();
            msg.Major = KAnp.Major;
            msg.Minor = KAnp.Minor;
            msg.ID = 0;
            msg.Type = KAnp.KANP_CMD_MGT_SELECT_ROLE;
            msg.AddUInt32(KAnp.KANP_KCD_ROLE_WORKSPACE);
            SafeSend(msg);
        }
    }

    /// <summary>
    /// This class implements the logic to communicate with the KCD in 
    /// workspace mode.
    /// </summary>
    public class KcdThread : KWorkerThread
    {
        /// <summary>
        /// Reference to the KCD broker.
        /// </summary>
        private WmKcdBroker m_broker;

        /// <summary>
        /// Tree of KCDs indexed by KCD ID.
        /// </summary>
        private SortedDictionary<KcdIdentifier, KcdThreadHost> m_kcdTree = new SortedDictionary<KcdIdentifier, KcdThreadHost>();

        /// <summary>
        /// List of KCDs that have been disconnected. There is one
        /// disconnection notice per KCD in this list.
        /// </summary>
        private List<KcdThreadHost> m_disconnectedList = new List<KcdThreadHost>();

        /// <summary>
        /// List of control messages to send to the WM.
        /// </summary>
        private List<KcdControlMsg> m_toWmControlMsgList = new List<KcdControlMsg>();

        /// <summary>
        /// List of ANP messages to send to the WM.
        /// </summary>
        private List<KcdAnpMsg> m_toWmAnpMsgList = new List<KcdAnpMsg>();

        /// <summary>
        /// True if a notification has been received from the WM.
        /// </summary>
        private bool m_wmNotifFlag = false;

        /// <summary>
        /// Quench deadline last obtained from the WM.
        /// </summary>
        private DateTime m_quenchDeadline;

        public KcdThread(WmKcdBroker broker)
        {
            m_broker = broker;
        }

        protected override void Run()
        {
            m_wmNotifFlag = true;
            m_quenchDeadline = DateTime.MinValue;
            ReplyToWm();
            MainLoop();
        }

        protected override void OnCompletion()
        {
            m_broker.OnKcdThreadCompletion(FailException);
        }

        /// <summary>
        /// Called when the WM has notified us that it has sent us messages.
        /// </summary>
        public void HandleKcdNotification()
        {
            m_wmNotifFlag = true;
        }

        /// <summary>
        /// Return true if some messages need to be sent to the WM.
        /// </summary>
        private bool MustReplyToWm()
        {
            Debug.Assert(m_disconnectedList.Count == 0 || m_toWmControlMsgList.Count > 0);
            return (m_toWmAnpMsgList.Count > 0 || m_toWmControlMsgList.Count > 0);
        }

        /// <summary>
        /// Dispatch the ANP and control messages to the WM, retrieve the
        /// latest quench deadline and clear disconnected KCDs.
        /// </summary>
        private void ReplyToWm()
        {
            if (!MustReplyToWm()) return;
            m_broker.SendMessagesToWm(m_toWmControlMsgList, m_toWmAnpMsgList, out m_quenchDeadline);
            foreach (KcdThreadHost kcd in m_disconnectedList) m_kcdTree.Remove(kcd.KcdID);
            m_toWmControlMsgList.Clear();
            m_toWmAnpMsgList.Clear();
            m_disconnectedList.Clear();
        }

        /// <summary>
        /// Process messages sent by the WM, update the quench deadline.
        /// </summary>
        private void ProcessIncomingWmMessages()
        {
            // Process the incoming messages.
            List<KcdControlMsg> controlArray;
            List<KcdAnpMsg> anpArray;
            m_broker.GetMessagesForThread(out controlArray, out anpArray, out m_quenchDeadline);
            foreach (KcdControlMsg msg in controlArray) ProcessWmControlMsg(msg);
            foreach (KcdAnpMsg msg in anpArray) ProcessWmAnpMsg(msg);

            // Send back control replies, if any.
            ReplyToWm();
        }

        /// <summary>
        /// Process a control message received from the WM.
        /// </summary>
        private void ProcessWmControlMsg(KcdControlMsg msg)
        {
            Debug.Assert(msg is KcdConnectionRequest);
            KcdConnectionRequest req = (KcdConnectionRequest)msg;

            // Handle new KCD to connect.
            if (req.ConnectFlag)
            {
                Debug.Assert(!m_kcdTree.ContainsKey(req.KcdID));
                m_kcdTree[req.KcdID] = new KcdThreadHost(req.KcdID);
            }

            // Disconnect the KCD, if we didn't disconnect it yet.
            else
            {
                if (!m_kcdTree.ContainsKey(req.KcdID)) return;
                KcdThreadHost kcd = m_kcdTree[req.KcdID];
                if (kcd.ConnStatus == KcdThreadConnStatus.Disconnected) return;
                HandleDisconnectedKcd(kcd, null);
            }
        }

        /// <summary>
        /// Process an ANP message received from the WM.
        /// </summary>
        private void ProcessWmAnpMsg(KcdAnpMsg msg)
        {
            // Ignore messages not destined to connected KCDs.
            if (!m_kcdTree.ContainsKey(msg.KcdID)) return;
            KcdThreadHost kcd = m_kcdTree[msg.KcdID];
            if (kcd.ConnStatus != KcdThreadConnStatus.Connected) return;

            // Enqueue the message.
            kcd.SendQueue.Enqueue(msg.Msg);
        }

        /// <summary>
        /// Mark the KCD as connected and add a control message for the KCD in
        /// the control message list.
        /// </summary>
        private void HandleConnectedKcd(KcdThreadHost k, UInt32 minor)
        {
            k.ConnStatus = KcdThreadConnStatus.Connected;
            m_toWmControlMsgList.Add(new KcdConnectionNotice(k.KcdID, minor));
        }

        /// <summary>
        /// Mark the KCD as disconnected, add a control message for the KCD in 
        /// the control message list and add the KCD to the disconnected list.
        /// </summary>
        private void HandleDisconnectedKcd(KcdThreadHost k, Exception ex)
        {
            if (ex != null) KLogging.Log(2, "KCD " + k.KcdID.Host + " exception: " + ex.Message);
            if (k.Tunnel != null) k.Tunnel.Disconnect();
            k.ConnStatus = KcdThreadConnStatus.Disconnected;
            k.Ex = new EAnpExKcdConn();
            m_toWmControlMsgList.Add(new KcdDisconnectionNotice(k.KcdID, k.Ex));
            m_disconnectedList.Add(k);
        }

        /// <summary>
        /// Loop processing KCDs.
        /// </summary>
        private void MainLoop()
        {
            while (true)
            {
                Debug.Assert(!MustReplyToWm());

                // Refresh the quench deadline if it depends on the amount of 
                // time elapsed.
                if (m_quenchDeadline != DateTime.MinValue && m_quenchDeadline != DateTime.MaxValue)
                    m_wmNotifFlag = true;

                // If we were notified, process the WM messages. This refreshes
                // the quench deadline.
                if (m_wmNotifFlag)
                {
                    m_wmNotifFlag = false;
                    ProcessIncomingWmMessages();
                }

                Debug.Assert(!MustReplyToWm());

                // Determine whether we are quenched and the value of the
                // select timeout. By default we wait forever in select().
                bool quenchFlag = false;
                int timeout = -2;

                // Be quenched until we are notified.
                if (m_quenchDeadline == DateTime.MaxValue)
                    quenchFlag = true;

                // Be quenched up to the deadline we were given.
                else if (m_quenchDeadline != DateTime.MinValue)
                {
                    DateTime now = DateTime.Now;
                    if (m_quenchDeadline > now)
                    {
                        quenchFlag = true;
                        timeout = (int)(m_quenchDeadline - now).TotalMilliseconds;
                    }
                }

                // Prepare the call to select.
                bool connWatchFlag = false;
                SelectSockets selectSockets = new SelectSockets();
                foreach (KcdThreadHost k in m_kcdTree.Values) PrepareStateForSelect(k, selectSockets,
                                                                             quenchFlag, ref connWatchFlag);

                // Our state has changed. Notify the WM and recompute our state.
                if (MustReplyToWm())
                {
                    ReplyToWm();
                    continue;
                }

                // Reduce the timeout to account for ktlstunnel.exe.
                if (connWatchFlag) DecrementTimeout(ref timeout, 300);

                // Block in the call to select(). Note that we receive notifications
                // here.
                selectSockets.Timeout = timeout * 1000;
                Block(selectSockets);

                // If we are not quenched, perform transfers.
                if (!quenchFlag)
                {
                    foreach (KcdThreadHost k in m_kcdTree.Values) UpdateStateAfterSelect(k, selectSockets);
                    ReplyToWm();
                }
            }
        }

        /// <summary>
        /// Add the socket of the KCD in the select sets as needed and manage
        /// ktlstunnel.exe processes.
        /// </summary>
        private void PrepareStateForSelect(KcdThreadHost k, SelectSockets selectSockets, bool quenchFlag,
                                           ref bool connWatchFlag)
        {
            // Note: the KCD should never have received a message when this function
            // is called. The function UpdateStateAfterSelect() is responsible for
            // doing all the transfers and handling any message received after these
            // transfers.
            try
            {
                k.CheckNoMessageReceivedInvariant();

                if (k.ConnStatus == KcdThreadConnStatus.Scheduled)
                {
                    // Start ktlstunnel.exe.
                    k.ConnStatus = KcdThreadConnStatus.Connecting;
                    k.Tunnel.BeginConnect();
                }

                if (k.ConnStatus == KcdThreadConnStatus.Connecting)
                {
                    // The TCP connection is now open.
                    if (k.Tunnel.CheckConnect())
                    {
                        // Send the select role command.
                        k.SendSelectRoleMsg();

                        // Wait for the reply to arrive.
                        k.ConnStatus = KcdThreadConnStatus.RoleReply;
                    }

                    // Wait for the TCP connection to be established. We busy wait
                    // to monitor the status of ktlstunnel.exe regularly, to detect
                    // the case where the connection fails.
                    else connWatchFlag = true;
                }

                if (k.ConnStatus == KcdThreadConnStatus.RoleReply)
                {
                    // Wait for the reply to arrive.
                    if (!quenchFlag) k.Tunnel.UpdateSelect(selectSockets);
                }

                if (k.ConnStatus == KcdThreadConnStatus.Connected)
                {
                    // Send the next message, if possible.
                    k.SendNextQueuedMsgIfNeeded();
                    if (!quenchFlag) k.Tunnel.UpdateSelect(selectSockets);
                }

                k.CheckNoMessageReceivedInvariant();
            }

            catch (Exception ex)
            {
                HandleDisconnectedKcd(k, ex);
            }
        }

        /// <summary>
        /// Analyse the result of the select() call for the specified KCD.
        /// </summary>
        private void UpdateStateAfterSelect(KcdThreadHost k, SelectSockets selectSockets)
        {
            try
            {
                k.CheckNoMessageReceivedInvariant();

                // We have nothing to do if we don't have an established TCP
                // connection.
                if (k.ConnStatus != KcdThreadConnStatus.Connected &&
                    k.ConnStatus != KcdThreadConnStatus.RoleReply) return;

                // Perform transfers only if the socket is ready.
                Debug.Assert(k.Tunnel.Sock != null);
                if (!selectSockets.InReadOrWrite(k.Tunnel.Sock)) return;

                // Do up to 20 transfers (the limit exists for quenching purposes).
                for (int i = 0; i < 20; i++)
                {
                    // Send a message if possible.
                    k.SendNextQueuedMsgIfNeeded();

                    // Remember if we are sending a message.
                    bool sendingFlag = k.Tunnel.IsSendingMessage();

                    // Do transfers.
                    k.Tunnel.DoXfer();

                    // Stop if no message has been received and no message has been sent.
                    if (!k.Tunnel.HasReceivedMessage() &&
                        (!sendingFlag || k.Tunnel.IsSendingMessage())) break;

                    // Process the message received.
                    if (k.Tunnel.HasReceivedMessage()) ProcessIncomingKcdMessage(k, k.Tunnel.GetMsg());
                }

                k.CheckNoMessageReceivedInvariant();
            }

            catch (Exception ex)
            {
                HandleDisconnectedKcd(k, ex);
            }
        }

        /// <summary>
        /// Handle a message received from a KCD.
        /// </summary>
        private void ProcessIncomingKcdMessage(KcdThreadHost k, AnpMsg msg)
        {
            if (k.ConnStatus == KcdThreadConnStatus.RoleReply)
            {
                if (msg.Type == KAnp.KANP_RES_FAIL_MUST_UPGRADE)
                    throw new EAnpExUpgradeKwm();

                else if (msg.Type != KAnp.KANP_RES_OK)
                    throw new Exception(msg.Elements[1].String);

                else if (msg.Minor < KAnp.LastCompMinor)
                    throw new Exception("The KCD at " + k.KcdID.Host +
                                        " is too old and needs to be upgraded.");

                else HandleConnectedKcd(k, Math.Min(msg.Minor, KAnp.Minor));
            }

            else m_toWmAnpMsgList.Add(new KcdAnpMsg(msg, k.KcdID));
        }

        /// <summary>
        /// Reduce the value of the timeout specified to the value
        /// specified. -2 means infinity.
        /// </summary>
        private void DecrementTimeout(ref int timeout, int value)
        {
            Debug.Assert(value >= 0);
            if (timeout == -2) timeout = value;
            else timeout = Math.Min(timeout, value);
        }
    }
}