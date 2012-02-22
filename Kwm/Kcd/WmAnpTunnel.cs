using kcslib;
using kwmlib;
using System;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace kwm
{
    // The code in this file sucks. Clean it up eventually.

    /// <summary>
    /// Add an ANP interface to the Tunnel.
    /// </summary>
    public class AnpTunnel
    {
        private AnpTransport transport = null;
        private Tunnel tunnel;

        public AnpTunnel(string _host, int _port)
        {
            tunnel = new Tunnel(_host, _port);
        }

        public Socket Sock
        {
            get { return tunnel.Sock; }
        }

        public AnpTransport GetTransport()
        {
            return transport;
        }

        public Tunnel GetTunnel()
        {
            return tunnel;
        }

        public void BeginConnect()
        {
            BeginConnect(null, 0);
        }

        /// <summary>
        /// Connect the tunnel to the remote host. If host != null,
        /// then ktlstunnel reconnects to this host when the local
        /// connection is closed.
        /// This is a non-blocking method.
        /// </summary>
        public void BeginConnect(string host, int port)
        {
            if (host == null || port == 0)
                tunnel.BeginTls("");
            else
                tunnel.BeginTls("-r " + host + ":" + port.ToString());
        }

        /// <summary>
        /// If the tunnel is connected, a new AnpTransport is created and the
        /// method returns true. Otherwise, the method returns false.
        /// </summary>
        public bool CheckConnect()
        {
            SelectSockets select = new SelectSockets();
            select.Timeout = 0;
            select.AddRead(tunnel.Sock);
            tunnel.CheckTls();
            select.Select();
            if (select.ReadSockets.Contains(tunnel.Sock))
            {
                CreateTransport();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Close the connection to ktlstunnel.
        /// </summary>
        public void Disconnect()
        {
            tunnel.Disconnect();
            transport = null;
        }

        /// <summary>
        /// Kill ktlstunnel.
        /// </summary>
        public void Terminate()
        {
            tunnel.Terminate();
            transport = null;
        }

        /// <summary>
        /// Create an AnpTransport when the tunnel is connected.
        /// </summary>
        public void CreateTransport()
        {
            transport = new AnpTransport(tunnel.EndTls());
            transport.BeginRecv();
        }

        /// <summary>
        /// Get an AnpMsg.
        /// </summary>
        public AnpMsg GetMsg()
        {
            Debug.Assert(transport.DoneReceiving);
            AnpMsg msg = transport.GetRecv();
            transport.BeginRecv();
            return msg;
        }

        /// <summary>
        /// Exchange messages with the server and return true if a message has
        /// been received.
        /// </summary>
        public bool CheckReceive()
        {
            Debug.Assert(transport != null, "transfert can't be null");
            Debug.Assert(transport.IsReceiving || transport.DoneReceiving);
            if (transport.IsSending || transport.IsReceiving)
                transport.DoXfer();
            return transport.DoneReceiving;
        }

        /// <summary>
        /// Exchange messages with the server and return true if a message is
        /// being sent.
        /// </summary>
        public bool CheckSend()
        {
            Debug.Assert(transport.IsReceiving || transport.DoneReceiving);
            if (transport.IsSending || transport.IsReceiving)
                transport.DoXfer();
            return transport.IsSending;
        }

        /// <summary>
        /// Return true if a message has been received.
        /// </summary>
        public bool HasReceivedMessage()
        {
            return transport.DoneReceiving;
        }

        /// <summary>
        /// Return true if a message is being sent.
        /// </summary>
        public bool IsSendingMessage()
        {
            return transport.IsSending;
        }

        /// <summary>
        /// Execute socket read and write operations.
        /// </summary>
        public void DoXfer()
        {
            transport.DoXfer();
        }

        /// <summary>
        /// Update the select set specified with the socket of the tunnel.
        /// </summary>
        public void UpdateSelect(SelectSockets selectSocket)
        {
            transport.UpdateSelect(selectSocket);
        }
    }

    /// <summary>
    /// Wrapper around ktlstunnel.
    /// </summary>
    public class Tunnel
    {
        public string Host;
        public int Port;

        public Socket Sock;

        public Tunnel(string host, int port)
        {
            Host = host;
            Port = port;
        }

        /// <summary>
        /// ktlstunnel.exe process.
        /// </summary>
        private KProcess TunnelProcess;

        public void BeginTls() { BeginTls(""); }

        /// <summary>
        /// Create a listening socket and spawn ktlstunnel process.
        /// </summary>
        public void BeginTls(string extraParams)
        {

            IPEndPoint endPoint = new IPEndPoint(IPAddress.Loopback, 0);
            Sock = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            Sock.Bind(endPoint);
            Sock.Listen(1);

            // Create a logging dir for ktlstunnel, if it does not exist.
            if (!Directory.Exists(KwmPath.GetKtlstunnelLogFilePath()))
            {
                Directory.CreateDirectory(KwmPath.GetKtlstunnelLogFilePath());
            }

            // Start ktlstunnel as such.
            // ktlstunnel localhost ((IPEndPoint)Listener.LocalEndPoint).Port Host Port [-r host:port]
            String loggingPath = "-L " + "\"" + KwmPath.GetKtlstunnelLogFilePath() + "ktlstunnel-" + KwmPath.GetLogFileName() + "\" ";
            String loggingLevel = "";

            if (KwmCfg.Cur.KtlstunnelLoggingLevel == 1)
            {
                loggingLevel = "-l minimal ";
                loggingLevel += loggingPath;
            }
            else if (KwmCfg.Cur.KtlstunnelLoggingLevel == 2)
            {
                loggingLevel = "-l debug ";
                loggingLevel += loggingPath;
            }

            String startupLine = "\"" + KwmPath.KwmKtlstunnelPath + "\" " +
                                 loggingLevel +
                                 "localhost " + ((IPEndPoint)Sock.LocalEndPoint).Port.ToString() + " " +
                                 Host + " " + Port + " " + extraParams;

            KLogging.Log("Starting ktlstunnel.exe : " + startupLine);

            TunnelProcess = new KProcess(startupLine);
            TunnelProcess.InheritHandles = false;
            TunnelProcess.CreationFlags = (uint)KSyscalls.CREATION_FLAGS.CREATE_NO_WINDOW;
            TunnelProcess.Start();
        }

        /// <summary>
        /// Accept the connection received from ktlstunnel.
        /// </summary>
        /// <returns></returns>
        public Socket EndTls()
        {
            Socket Listener = Sock;
            Sock = Listener.Accept();
            Listener.Close();
            Sock.Blocking = false;
            return Sock;
        }

        /// <summary>
        /// Check if ktlstunnel process is still running.
        /// </summary>
        public void CheckTls()
        {
            if (!TunnelProcess.IsRunning())
                throw new AnpException("Cannot establish connection");
        }

        /// <summary>
        /// Close the socket if it is opened.
        /// </summary>
        public void Disconnect()
        {
            if (Sock != null)
            {
                try
                {
                    Sock.Close();
                }
                catch (Exception)
                { }
                Sock = null;
            }
        }

        /// <summary>
        /// Disconnect and close ktlstunnel.
        /// </summary>
        public void Terminate()
        {
            Disconnect();

            if (TunnelProcess != null)
            {
                TunnelProcess.Terminate();
                TunnelProcess = null;
            }
        }
    }

    /// <summary>
    /// Represent a worker thread that runs a tunnel.
    /// </summary>
    public abstract class KwmTunnelThread : KWorkerThread
    {
        /// <summary>
        /// Host to connect to.
        /// </summary>
        protected String Host;

        /// <summary>
        /// Port to connect to.
        /// </summary>
        protected int Port;

        /// <summary>
        /// Host to reconnect to when the local connection is closed. This is
        /// null if no reconnection is needed.
        /// </summary>
        protected String ReconnectHost;

        /// <summary>
        /// Port to reconnect to when the local connection is closed. This is 0
        /// if no reconnection is needed.
        /// </summary>
        protected int ReconnectPort;

        /// <summary>
        /// Internal ANP tunnel.
        /// </summary>
        protected AnpTunnel InternalAnpTunnel;

        public KwmTunnelThread(String host, int port, String reconnectHost, int reconnectPort)
        {
            Host = host;
            Port = port;
            ReconnectHost = reconnectHost;
            ReconnectPort = reconnectPort;
        }

        /// <summary>
        /// This method is called once the tunnel has been connected.
        /// </summary>
        protected abstract void OnTunnelConnected();

        /// <summary>
        /// Retrieve a message from the tunnel.
        /// </summary>
        protected AnpMsg GetAnpMsg()
        {
            AnpTransport transfer = InternalAnpTunnel.GetTransport();
            Debug.Assert(transfer.IsReceiving || transfer.DoneReceiving);
            Debug.Assert(!transfer.IsSending);

            while (!transfer.DoneReceiving)
            {
                transfer.DoXfer();

                if (!transfer.DoneReceiving)
                {
                    SelectSockets set = new SelectSockets();
                    InternalAnpTunnel.UpdateSelect(set);
                    Block(set);
                }
            }

            AnpMsg msg = transfer.GetRecv();
            transfer.BeginRecv();
            return msg;
        }

        /// <summary>
        /// Return true if an ANP message has been received.
        /// </summary>
        protected bool AnpMsgReceived()
        {
            return InternalAnpTunnel.CheckReceive();
        }

        /// <summary>
        /// Send a message on the tunnel.
        /// </summary>
        protected void SendAnpMsg(AnpMsg m)
        {
            AnpTransport transfer = InternalAnpTunnel.GetTransport();
            Debug.Assert(transfer.IsReceiving || transfer.DoneReceiving);
            Debug.Assert(!transfer.IsSending);
            transfer.SendMsg(m);

            while (transfer.IsSending)
            {
                transfer.DoXfer();

                if (transfer.IsSending)
                {
                    SelectSockets set = new SelectSockets();
                    InternalAnpTunnel.UpdateSelect(set);
                    Block(set);
                }
            }
        }

        protected override void Run()
        {
            // Connect the tunnel.
            InternalAnpTunnel = new AnpTunnel(Host, Port);
            Tunnel tunnel = InternalAnpTunnel.GetTunnel();
            InternalAnpTunnel.BeginConnect(ReconnectHost, ReconnectPort);

            while (true)
            {
                SelectSockets set = new SelectSockets();
                set.Timeout = 100;
                tunnel.CheckTls();
                set.AddRead(tunnel.Sock);
                Block(set);
                if (set.ReadSockets.Contains(tunnel.Sock))
                {
                    InternalAnpTunnel.CreateTransport();
                    break;
                }
            }

            // Handle the tunnel.
            OnTunnelConnected();
        }

        protected override void OnCompletion()
        {
            if (InternalAnpTunnel != null)
                InternalAnpTunnel.Disconnect();
        }
    }
}