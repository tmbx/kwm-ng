using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace kwm
{
    /// <summary>
    /// Status of the connection with the KCD.
    /// </summary>
    public enum KcdConnStatus
    {
        /// <summary>
        /// The workspace is not connected.
        /// </summary>
        Disconnected,

        /// <summary>
        /// A request has been sent to disconnect the workspace.
        /// </summary>
        Disconnecting,

        /// <summary>
        /// The workspace is connected.
        /// </summary>
        Connected,

        /// <summary>
        /// A request has been sent to connect the workspace.
        /// </summary>
        Connecting
    }

    /// <summary>
    /// This class provides an identifier for a KCD server. The identifier
    /// consists of the host name and the port of the KCD server. Since this
    /// object is shared between threads without locking, it is immutable.
    /// </summary>
    public class KcdIdentifier : IComparable
    {
        private String m_host;
        private UInt16 m_port;

        public String Host { get { return m_host; } }
        public UInt16 Port { get { return m_port; } }

        public KcdIdentifier(String host, UInt16 port)
        {
            m_host = host;
            m_port = port;
        }

        public int CompareTo(Object obj)
        {
            KcdIdentifier kcd = (KcdIdentifier)obj;

            int r = kcd.Host.CompareTo(Host);
            if (r != 0) return r;

            return kcd.Port.CompareTo(Port);
        }
    }

    /// <summary>
    /// This class represents an ANP message delivered to/from a KCD
    /// by the KCD broker.
    /// </summary>
    public class KcdAnpMsg
    {
        /// <summary>
        /// The ANP message being delivered.
        /// </summary>
        public AnpMsg Msg;

        /// <summary>
        /// Associated KCD.
        /// </summary>
        public KcdIdentifier KcdID;

        public KcdAnpMsg(AnpMsg msg, KcdIdentifier kcdID)
        {
            Msg = msg;
            KcdID = kcdID;
        }

        /// <summary>
        /// Return true if the message is an ANP reply.
        /// </summary>
        public bool IsReply()
        {
            return ((Msg.Type & KAnp.ROLE_MASK) == KAnp.KANP_RES);
        }

        /// <summary>
        /// Return true if the message is an ANP event.
        /// </summary>
        public bool IsEvent()
        {
            return ((Msg.Type & KAnp.ROLE_MASK) == KAnp.KANP_EVT);
        }
    }

    /// <summary>
    /// This class represents a control message exchanged between the WM and
    /// the broker.
    /// </summary>
    public class KcdControlMsg { }

    /// <summary>
    /// This class represents a KCD connect/disconnect request.
    /// </summary>
    public class KcdConnectionRequest : KcdControlMsg
    {
        /// <summary>
        /// ID of the KCD to connect/disconnect.
        /// </summary>
        public KcdIdentifier KcdID;

        /// <summary>
        /// True if connection is required.
        /// </summary>
        public bool ConnectFlag;

        public KcdConnectionRequest(KcdIdentifier kcdID, bool connectFlag)
        {
            KcdID = kcdID;
            ConnectFlag = connectFlag;
        }
    }

    /// <summary>
    /// This class represents a KCD connection notice. Used when
    /// a KCD is now in the connected state.
    /// </summary>
    public class KcdConnectionNotice : KcdControlMsg
    {
        /// <summary>
        /// ID of the KCD that is now connected.
        /// </summary>
        public KcdIdentifier KcdID;

        /// <summary>
        /// Minor version of the protocol spoken with the KCD.
        /// </summary>
        public UInt32 MinorVersion;

        public KcdConnectionNotice(KcdIdentifier kcdID, UInt32 minorVersion)
        {
            KcdID = kcdID;
            MinorVersion = minorVersion;
        }
    }

    /// <summary>
    /// This class represents a KCD disconnection notice. Used when
    /// a KCD is now in the disconnected state.
    /// </summary>
    public class KcdDisconnectionNotice : KcdControlMsg
    {
        /// <summary>
        /// ID of the KCD that is now disconnected.
        /// </summary>
        public KcdIdentifier KcdID;

        /// <summary>
        /// If the disconnection was caused by an error, this
        /// is the exception describing the error.
        /// </summary>
        public Exception Ex;

        public KcdDisconnectionNotice(KcdIdentifier kcdID, Exception ex)
        {
            KcdID = kcdID;
            Ex = ex;
        }
    }

    /// <summary>
    /// This message is posted to the UI thread by the broker to wake-up
    /// the WM.
    /// </summary>
    public class KcdWmWakeUpMsg : KThreadMsg
    {
        private WmKcdBroker Broker;

        public KcdWmWakeUpMsg(WmKcdBroker broker)
        {
            Broker = broker;
        }

        public override void Run()
        {
            Broker.HandleWmWakeUp(this);
        }
    }

    /// <summary>
    /// This message is posted to the KCD thread by the broker to wake-up
    /// the KCD thread.
    /// </summary>
    public class KcdThreadWakeUpMsg : KThreadMsg
    {
        private WmKcdBroker Broker;

        public KcdThreadWakeUpMsg(WmKcdBroker broker)
        {
            Broker = broker;
        }

        public override void Run()
        {
            Broker.HandleKcdThreadWakeUp(this);
        }
    }

    /// <summary>
    /// Delegate type called from the OnCompletion() handler of the KCD
    /// thread.
    /// </summary>
    public delegate void KcdCompletionDelegate(bool successFlag, Exception ex);

    /// <summary>
    /// This class manages the interactions between the KCD thread and the
    /// workspace manager. It encapsulates most synchronization and flow 
    /// control issues.
    /// </summary>
    public class WmKcdBroker
    {
        /// <summary>
        /// Quench if that many messages are lingering in the WM ANP message
        /// queue.
        /// </summary>
        private const UInt32 m_quenchQueueMaxSize = 50;

        /// <summary>
        /// Number of messages to post to the WM between each quench check.
        /// </summary>
        private const UInt32 m_quenchBatchCount = 100;

        /// <summary>
        /// Rate at which messages will be processed, e.g. 1 message per 2
        /// milliseconds.
        /// </summary>
        private const UInt32 m_quenchProcessRate = 5;

        /// <summary>
        /// Fired when a notification has been received from the KCD thread or
        /// the KCD thread has completed.
        /// </summary>
        public event EventHandler<EventArgs> OnEvent;

        /// <summary>
        /// Reference to the KCD thread.
        /// </summary>
        private KcdThread m_thread = null;

        /// <summary>
        /// Mutex protecting the variables that follow.
        /// </summary>
        private Object m_mutex = new Object();

        /// <summary>
        /// Message posted to wake-up the WM.
        /// </summary>
        private KcdWmWakeUpMsg m_wmWakeUpMsg = null;

        /// <summary>
        /// Message posted to wake-up the KCD thread.
        /// </summary>
        private KcdThreadWakeUpMsg m_threadWakeUpMsg = null;

        /// <summary>
        /// Array of control messages posted to the WM.
        /// </summary>
        private List<KcdControlMsg> m_toWmControlMsgArray = null;

        /// <summary>
        /// Array of control messages posted to the KCD thread.
        /// </summary>
        private List<KcdControlMsg> m_toThreadControlMsgArray = null;

        /// <summary>
        /// Array of ANP message posted to the WM.
        /// </summary>
        private List<KcdAnpMsg> m_toWmAnpMsgArray = null;

        /// <summary>
        /// Array of ANP message posted to the KCD thread.
        /// </summary>
        private List<KcdAnpMsg> m_toThreadAnpMsgArray = null;

        /// <summary>
        /// Number of messages that have been processed in the current batch.
        /// </summary>
        private UInt32 m_currentBatchCount = 0;

        /// <summary>
        /// Date at which the current batch has been started.
        /// </summary>
        private DateTime m_currentBatchStartDate = DateTime.MinValue;

        public WmKcdBroker()
        {
            CleanUp();
        }

        /// <summary>
        /// Reset the state to the initial state.
        /// </summary>
        private void CleanUp()
        {
            m_toWmControlMsgArray = new List<KcdControlMsg>();
            m_toThreadControlMsgArray = new List<KcdControlMsg>();
            m_toWmAnpMsgArray = new List<KcdAnpMsg>();
            m_toThreadAnpMsgArray = new List<KcdAnpMsg>();
            m_currentBatchCount = 0;
            m_currentBatchStartDate = DateTime.MinValue;
        }

        /// <summary>
        /// Notify the WM that something occurred. Assume mutex is locked.
        /// </summary>
        private void NotifyWm()
        {
            if (m_wmWakeUpMsg == null)
            {
                m_wmWakeUpMsg = new KcdWmWakeUpMsg(this);
                KBase.ExecInUI(new KBase.EmptyDelegate(m_wmWakeUpMsg.Run));
            }
        }

        /// <summary>
        /// Notify the KCD that something occurred. Assume mutex is locked.
        /// </summary>
        private void NotifyKcdThread()
        {
            if (m_thread != null && m_threadWakeUpMsg == null)
            {
                m_threadWakeUpMsg = new KcdThreadWakeUpMsg(this);
                m_thread.PostToWorker(m_threadWakeUpMsg);
            }
        }

        /// <summary>
        /// Recompute the quench deadline returned to the KCD thread.
        /// Assume mutex is locked.
        /// </summary>
        private DateTime RecomputeQuenchDeadline()
        {
            // Too many unprocessed messages.
            if (m_toWmAnpMsgArray.Count >= m_quenchQueueMaxSize) return DateTime.MaxValue;

            // Batch check count not yet reached.
            if (m_currentBatchCount < m_quenchBatchCount) return DateTime.MinValue;

            // Compute deadline.
            DateTime deadline = m_currentBatchStartDate.AddMilliseconds(m_currentBatchCount * m_quenchProcessRate);
            DateTime now = DateTime.Now;

            // Enough time has passed during the processsing of the batch.
            // Reset the batch statistics.
            if (deadline < now)
            {
                m_currentBatchCount = 0;
                m_currentBatchStartDate = now;
                return DateTime.MinValue;
            }

            // Not enough time has passed to process the batch. Return the
            // deadline computed.
            return deadline;
        }

        /// <summary>
        /// Notify the listeners that something occurred.
        /// </summary>
        private void DoOnEvent()
        {
            if (OnEvent != null) OnEvent(this, null);
        }


        ////////////////////////////////////////////
        // Interface methods for internal events. //
        ////////////////////////////////////////////

        /// <summary>
        /// Internal handler for KcdWmWakeUpMsg.
        /// </summary>
        public void HandleWmWakeUp(KcdWmWakeUpMsg msg)
        {
            lock (m_mutex)
            {
                // Clear the posted message reference.
                Debug.Assert(m_wmWakeUpMsg == msg);
                m_wmWakeUpMsg = null;
            }

            // Notify the WM state machine that we have something for it.
            DoOnEvent();
        }

        /// <summary>
        /// Internal handler for KcdThreadWakeUpMsg.
        /// </summary>
        public void HandleKcdThreadWakeUp(KcdThreadWakeUpMsg msg)
        {
            lock (m_mutex)
            {
                // Clear the posted message reference.
                Debug.Assert(m_threadWakeUpMsg == msg);
                m_threadWakeUpMsg = null;
            }

            // Notify the KCD that we have something for it.
            m_thread.HandleKcdNotification();
        }


        ///////////////////////////////////
        // Interface methods for the WM. //
        ///////////////////////////////////

        /// <summary>
        /// Start the KCD broker.
        /// </summary>
        public void Start()
        {
            Debug.Assert(m_thread == null);
            m_thread = new KcdThread(this);
            m_thread.Start();
        }
        /// <summary>
        /// This method is called to stop the KCD broker. It returns true when
        /// the broker has stopped.
        /// </summary>
        public bool TryStop()
        {
            if (m_thread != null)
            {
                m_thread.RequestCancellation();
                return false;
            }

            // Clean up the data structures.
            CleanUp();

            return true;
        }

        /// <summary>
        /// Request a KCD to be connected.
        /// </summary>
        public void RequestKcdConnect(KcdIdentifier kcdID)
        {
            lock (m_mutex)
            {
                // The following sequence of events can happen:
                // - KCD thread posts a disconnection event.
                // - WM posts an ANP message.
                // - WM receives a disconnection event.
                // - WM posts a connection request.
                // - KCD thread receives the connection request and the ANP message concurrently,
                //   possibly posting the ANP message incorrectly.
                // To prevent this situation, we ensure that we have no lingering
                // ANP message left for that KCD.
                List<KcdAnpMsg> newList = new List<KcdAnpMsg>();

                foreach (KcdAnpMsg m in m_toThreadAnpMsgArray)
                {
                    if (m.KcdID != kcdID) newList.Add(m);
                }

                m_toThreadAnpMsgArray = newList;

                m_toThreadControlMsgArray.Add(new KcdConnectionRequest(kcdID, true));
                NotifyKcdThread();
            }
        }

        /// <summary>
        /// Request a KCD to be disconnected.
        /// </summary>
        public void RequestKcdDisconnect(KcdIdentifier kcdID)
        {
            lock (m_mutex)
            {
                m_toThreadControlMsgArray.Add(new KcdConnectionRequest(kcdID, false));
                NotifyKcdThread();
            }
        }

        /// <summary>
        /// Send an ANP message to a KCD.
        /// </summary>
        public void SendAnpMsgToKcd(KcdAnpMsg m)
        {
            lock (m_mutex)
            {
                m_toThreadAnpMsgArray.Add(m);
                NotifyKcdThread();
            }
        }

        /// <summary>
        /// Return the messages posted by the KCD thread.
        /// </summary>
        public void GetMessagesForWm(out List<KcdControlMsg> controlArray, out List<KcdAnpMsg> anpArray)
        {
            lock (m_mutex)
            {
                // Notify KCD if it was potentially quenched.
                if (m_toWmAnpMsgArray.Count >= m_quenchQueueMaxSize) NotifyKcdThread();

                controlArray = m_toWmControlMsgArray;
                m_toWmControlMsgArray = new List<KcdControlMsg>();
                anpArray = m_toWmAnpMsgArray;
                m_toWmAnpMsgArray = new List<KcdAnpMsg>();
            }
        }


        ///////////////////////////////////////////
        // Interface methods for the KCD thread. //
        //////////////////////////////////////////

        /// <summary>
        /// Return the messages posted by the KCD and the current quench
        /// deadline.
        /// </summary>
        public void GetMessagesForThread(out List<KcdControlMsg> controlArray, out List<KcdAnpMsg> anpArray,
                                         out DateTime quenchDeadline)
        {
            lock (m_mutex)
            {
                controlArray = m_toThreadControlMsgArray;
                m_toThreadControlMsgArray = new List<KcdControlMsg>();
                anpArray = m_toThreadAnpMsgArray;
                m_toThreadAnpMsgArray = new List<KcdAnpMsg>();
                quenchDeadline = RecomputeQuenchDeadline();
            }
        }

        /// <summary>
        /// Send control and ANP messages to the WM, return current quench
        /// deadline.
        /// </summary>
        public void SendMessagesToWm(List<KcdControlMsg> controlArray, List<KcdAnpMsg> anpArray,
                                     out DateTime quenchDeadline)
        {
            lock (m_mutex)
            {
                m_toWmControlMsgArray.AddRange(controlArray);
                m_toWmAnpMsgArray.AddRange(anpArray);
                m_currentBatchCount += (UInt32)anpArray.Count;
                quenchDeadline = RecomputeQuenchDeadline();
                NotifyWm();
            }
        }

        /// <summary>
        /// Called when the KCD thread completes.
        /// </summary>
        public void OnKcdThreadCompletion(Exception ex)
        {
            m_thread = null;
            if (ex != null) KBase.HandleException(ex, true);
            DoOnEvent();
        }
    }
}
