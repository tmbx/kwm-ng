using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using System.Net;
using Microsoft.Win32;

namespace kwm
{
    /// <summary>
    /// Message sent to a KMOD thread to execute a command.
    /// </summary>
    public class KmodThreadCommand : KThreadMsg
    {
        /// <summary>
        /// K3P message to send to KMOD.
        /// </summary>
        public K3pMsg Msg;

        /// <summary>
        /// True if a result is associated to the command.
        /// </summary>
        public bool HaveResultFlag;

        // True if the result has been received. This is used by the broker.
        public bool ResultReadyFlag = false;

        /// <summary>
        /// Reference to the thread.
        /// </summary>
        private KmodThread m_thread;

        public KmodThreadCommand(KmodThread thread, K3pMsg msg, bool haveResultFlag)
        {
            m_thread = thread;
            Msg = msg;
            HaveResultFlag = haveResultFlag;
        }

        public override void Run()
        {
            m_thread.PostCommand(this);
        }
    }

    /// <summary>
    /// Message sent to the UI thread when the broker must be notified that the
    /// results of a command are ready.
    /// </summary>
    public class KmodThreadNotif : KThreadMsg
    {
        /// <summary>
        /// Reference to the broker.
        /// </summary>
        private WmKmodBroker m_broker;

        /// <summary>
        /// Associated command.
        /// </summary>
        private KmodThreadCommand m_command;

        public KmodThreadNotif(WmKmodBroker broker, KmodThreadCommand command)
        {
            m_broker = broker;
            m_command = command;
        }

        public override void Run()
        {
            m_broker.OnThreadNotification(m_command);
        }
    }

    /// <summary>
    /// Thread managing a KMOD process.
    /// </summary>
    public class KmodThread : KWorkerThread
    {
        /// <summary>
        /// True if the connect result have been received. This is used by
        /// the UI thread only.
        /// </summary>
        public bool HaveConnectResultFlag = false;

        /// <summary>
        /// Monitor used to synchronize the communication between the KMOD 
        /// broker and this worker thread.
        /// </summary>
        public Object Mon = new Object();

        /// <summary>
        /// Queue of K3P elements received from KMOD. The access to the queue
        /// is protected by locking the monitor. The worker thread pulses
        /// the monitor when a K3P element is queued.
        /// </summary>
        private Queue<K3pElement> m_elementQueue = new Queue<K3pElement>();

        /// <summary>
        /// Exception describing the error that occurred, if any. This is
        /// protected by the monitor. The worker thread pulses the monitor 
        /// when an error has occurred.
        /// </summary>
        public Exception Ex = null;

        /// <summary>
        /// Reference to the broker.
        /// </summary>
        private WmKmodBroker m_broker = null;

        /// <summary>
        /// Queue of commands posted to the thread.
        /// </summary>
        private Queue<KmodThreadCommand> m_commandQueue = new Queue<KmodThreadCommand>();

        /// <summary>
        /// KMOD process reference.
        /// </summary>
        private KProcess m_kmodProc = null;

        /// <summary>
        /// Reference to the KMOD socket.
        /// </summary>
        private Socket m_kmodSock = null;

        /// <summary>
        /// KMOD K3P transport.
        /// </summary>
        private K3pTransport m_transport = null;

        public KmodThread(WmKmodBroker broker)
            : base()
        {
            m_broker = broker;
        }

        /// <summary>
        /// Post the next command.
        /// </summary>
        public void PostCommand(KmodThreadCommand command)
        {
            m_commandQueue.Enqueue(command);
        }

        /// <summary>
        /// Return the next K3P element of the current command.
        /// </summary>
        public K3pElement GetNextK3pElement()
        {
            lock (Mon)
            {
                while (true)
                {
                    // There is an element ready.
                    if (m_elementQueue.Count > 0) return m_elementQueue.Dequeue();

                    // The thread failed during its processing. The error
                    // message has been posted to the UI thread, but we did
                    // not receive it yet, so throw it now.
                    if (Ex != null)
                    {
                        Exception ex = Ex;
                        Ex = null;
                        throw ex;
                    }

                    // It's taking KMOD too long to give us our next element.
                    if (!Monitor.Wait(Mon, 1000)) throw new Exception("KMOD timeout");
                }
            }
        }

        protected override void OnCompletion()
        {
            m_broker.OnThreadCompletion();
        }

        protected override void Run()
        {
            try
            {
                StartKmod();
                while (true) RunPass();
            }

            catch (KWorkerCancellationException)
            {
                throw;
            }

            catch (Exception ex)
            {
                lock (Mon)
                {
                    // Catch the exception and pulse.
                    Ex = ex;
                    Monitor.Pulse(Mon);
                }
            }

            finally
            {
                CleanUpKmod();
            }
        }

        /// <summary>
        /// Run one pass of the main loop.
        /// </summary>
        private void RunPass()
        {
            // Wait for a command.
            while (m_commandQueue.Count == 0) Block(new SelectSockets());

            // Get the next command.
            KmodThreadCommand command = m_commandQueue.Dequeue();

            // Send the command.
            m_transport.sendMsg(command.Msg);
            while (true)
            {
                m_transport.doXfer();
                if (!m_transport.isSending) break;
                SelectSockets select = new SelectSockets();
                select.AddWrite(m_kmodSock);
                Block(select);
            }

            // We're done.
            if (!command.HaveResultFlag) return;

            // Wait for the results or the next command.
            bool firstFlag = true;
            while (m_commandQueue.Count == 0)
            {
                if (!m_transport.isReceiving) m_transport.beginRecv();
                m_transport.doXfer();

                // Push the next result.
                if (m_transport.doneReceiving)
                {
                    lock (Mon)
                    {
                        m_elementQueue.Enqueue(m_transport.getRecv());
                        Monitor.Pulse(Mon);
                    }

                    // Send the notification that results are ready for this command.
                    if (firstFlag)
                    {
                        firstFlag = false;
                        PostToUI(new KmodThreadNotif(m_broker, command));
                    }
                }

                // Wait for the next result or the next command.
                else
                {
                    SelectSockets select = new SelectSockets();
                    select.AddRead(m_kmodSock);
                    Block(select);
                }
            }
        }

        /// <summary>
        /// Start kmod and connect to it.
        /// </summary>
        private void StartKmod()
        {
            FileStream file = null;
            Socket listenSock = null;
            RegistryKey kwmRegKey = null;

            try
            {
                // Get the path to the kmod executable in the registry.
                kwmRegKey = KwmReg.GetKwmLMRegKey();
                String Kmod = "\"" + (String)kwmRegKey.GetValue("InstallDir", @"C:\Program Files\Teambox\Teambox Manager") + "\\kmod\\kmod.exe\"";

                // The directory where KMOD will save logs and its database for use with the kwm.
                String KmodDir = KwmPath.GetKmodDirPath();
                Directory.CreateDirectory(KmodDir);

                // Start listening for kmod to connect when it'll be started.
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Loopback, 0);
                listenSock = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                listenSock.Bind(endPoint);
                listenSock.Listen(1);
                int port = ((IPEndPoint)listenSock.LocalEndPoint).Port;

                // Start KMOD in debugging mode if our settings say so.
                String debug = KwmCfg.Cur.KtlstunnelLoggingLevel > 0 ? " -l 3" : "";
                String args = " -C kmod_connect -p " + port + debug + " -m 20000 -k \"" + KmodDir + "\"";
                String cmdLine = Kmod + args;
                KLogging.Log("About to start kmod.exe: " + cmdLine);
                m_kmodProc = new KProcess(cmdLine);
                m_kmodProc.InheritHandles = false;
                m_kmodProc.CreationFlags = (uint)KSyscalls.CREATION_FLAGS.CREATE_NO_WINDOW;
                m_kmodProc.Start();

                // Wait for KMOD to connect for about 10 seconds.
                DateTime startTime = DateTime.Now;
                while (true)
                {
                    SelectSockets select = new SelectSockets();
                    select.AddRead(listenSock);
                    SetSelectTimeout(startTime, 10000, select, "no KMOD connection received");
                    Block(select);

                    if (select.InRead(listenSock))
                    {
                        m_kmodSock = listenSock.Accept();
                        m_kmodSock.Blocking = false;
                        break;
                    }
                }

                // Read the authentication data.
                byte[] authSockData = new byte[32];
                byte[] authFileData = new byte[32];

                startTime = DateTime.Now;
                int nbRead = 0;
                while (nbRead != 32)
                {
                    SelectSockets select = new SelectSockets();
                    select.AddRead(m_kmodSock);
                    SetSelectTimeout(startTime, 2000, select, "no authentication data received");
                    Block(select);
                    int r = KSocket.SockRead(m_kmodSock, authSockData, nbRead, 32 - nbRead);
                    if (r > 0) nbRead += r;
                }

                file = File.Open(KmodDir + "\\connect_secret", FileMode.Open);
                file.Read(authFileData, 0, 32);
                if (!KUtil.ByteArrayEqual(authFileData, authSockData))
                    throw new Exception("invalid authentication data received");

                // Set the transport.
                m_transport = new K3pTransport(m_kmodSock);
            }

            finally
            {
                if (file != null) file.Close();
                if (listenSock != null) listenSock.Close();
                if (kwmRegKey != null) kwmRegKey.Close();
            }
        }

        /// <summary>
        /// Helper method for StartKmod().
        /// </summary>
        private void SetSelectTimeout(DateTime startTime, int msec, SelectSockets select, String msg)
        {
            int remaining = msec - (int)(DateTime.Now - startTime).TotalMilliseconds;
            if (remaining < 0) throw new Exception(msg);
            select.Timeout = remaining * 1000;
        }

        /// <summary>
        /// Clean up the KMOD process and socket.
        /// </summary>
        private void CleanUpKmod()
        {
            if (m_kmodProc != null)
            {
                m_kmodProc.Terminate();
                m_kmodProc = null;
            }

            if (m_kmodSock != null)
            {
                m_kmodSock.Close();
                m_kmodSock = null;
            }
        }
    }
}
