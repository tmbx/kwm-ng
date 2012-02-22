using kcslib;
using kwmlib;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Timers;
using UserInactivityMonitoring;

namespace kwm
{
    /// <summary>
    /// Status of a VNC session.
    /// </summary>
    public enum VncSessionStatus
    {
        /// <summary>
        /// Initial step of the session.
        /// </summary>
        Initial,

        /// <summary>
        /// Waiting for a ticket.
        /// </summary>
        Ticket,

        /// <summary>
        /// Waiting for the main VNC process to start.
        /// </summary>
        MainProcess,

        /// <summary>
        /// Waiting for the dummy VNC process to start.
        /// </summary>
        DummyProcess,

        /// <summary>
        /// Waiting for the tunnel negociation to be completed.
        /// </summary>
        Tunnel,

        /// <summary>
        /// The session is running.
        /// </summary>
        Started,

        /// <summary>
        /// The session has completed.
        /// </summary>
        Completed
    }

    /// <summary>
    /// Backend class for the screen sharing application.
    /// </summary>
    public class AppVnc : KwsApp
    {
        /// <summary>
        /// This flag indicates if a VNC session has been started in one of the
        /// VNC applications. This flag is shared between all VNC applications.
        /// </summary>
        public static bool SessionPresentFlag = false;

        /// <summary>
        /// Reference to the local session, if any.
        /// </summary>
        public VncLocalSession LocalSession = null;

        public override UInt32 AppID { get { return KAnp.KANP_NS_VNC; } }

        public override KwsAnpEventStatus HandleAnpEvent(AnpMsg evt)
        {
            if (evt.Type == KAnp.KANP_EVT_VNC_START)
            {
                UInt64 date = evt.Elements[1].UInt64;
                UInt32 userID = evt.Elements[2].UInt32;
                UInt64 sessionID = evt.Elements[3].UInt64;
                String subject = evt.Elements[4].String;
                
                AnpMsg etEvt = Kws.MakePermEAnpEvent(EAnpEvt.VncSessionStarted, date, userID);
                etEvt.AddUInt64(sessionID);
                etEvt.AddString(subject);
                Kws.PostPermEAnpEvent(etEvt);

                return KwsAnpEventStatus.Processed;
            }

            else if (evt.Type == KAnp.KANP_EVT_VNC_END)
            {
                UInt64 date = evt.Elements[1].UInt64;
                UInt32 userID = evt.Elements[2].UInt32;
                UInt64 sessionID = evt.Elements[3].UInt64;
                
                AnpMsg etEvt = Kws.MakePermEAnpEvent(EAnpEvt.VncSessionEnded, date, userID);
                etEvt.AddUInt64(sessionID);
                Kws.PostPermEAnpEvent(etEvt);

                // If we have a local session, notify it about the event.
                if (LocalSession != null)
                {
                    EAnpException ex = null;
                    if (evt.Minor >= 5) ex = EAnpException.FromKAnpFailure(evt, 4);
                    else ex = new EAnpExGeneric("session closed");
                    LocalSession.OnSessionEndEventReceived(sessionID, ex);
                }

                return KwsAnpEventStatus.Processed;
            }

            return KwsAnpEventStatus.Unprocessed;
        }

        public override void Stop(Exception ex)
        {
            if (LocalSession != null) LocalSession.HandleSessionTrouble(ex);
            base.Stop(ex);
        }

        public override void OnKwsSmNotif(Object sender, KwsSmNotif notif)
        {
            // We want to stop the local session if we go offline.
            if (LocalSession != null &&
                !IsOnlineCapable() &&
                notif is KwsSmNotifKcdLogin)
            {
                KwsSmNotifKcdLogin n = notif as KwsSmNotifKcdLogin;
                LocalSession.HandleSessionTrouble(n.Ex);
            }
        }

        /// <summary>
        /// Start a server session. Throw on error. Return the session UUID.
        /// </summary>
        public byte[] StartServerSession(bool supportFlag, int windowHandle, String subject)
        {
            return StartSession(true, supportFlag, windowHandle, subject, 0);
        }

        /// <summary>
        /// Start a client session. Throw on error. Return the session UUID.
        /// </summary>
        public byte[] StartClientSession(UInt64 sessionID, String subject)
        {
            return StartSession(false, false, 0, subject, sessionID);
        }

        /// <summary>
        /// Helper method to start a session.
        /// </summary>
        private byte[] StartSession(bool serverSessionFlag, bool supportFlag, int windowHandle, String subject, UInt64 sessionID)
        {
            // Throw if we cannot open the session.
            if (AppVnc.SessionPresentFlag) throw new Exception("a screen sharing session is already running");
            if (!IsOnlineCapable()) throw new Exception("the " + KwmStrings.Kws + " is not connected to the server");

            // Remember that we have started a session.
            SessionPresentFlag = true;

            // Create the local session object.
            LocalSession = new VncLocalSession(this);
            LocalSession.ServerSessionFlag = serverSessionFlag;
            LocalSession.SupportSessionFlag = supportFlag;
            LocalSession.WindowHandle = windowHandle;
            LocalSession.Subject = subject;
            LocalSession.SessionID = sessionID;

            // Asynchronously start the session.
            KBase.ExecInUI(LocalSession.HandleNextSessionStep);

            Kws.OnStateChange(WmStateChange.Transient);

            return LocalSession.SessionUuid;
        }
    }

    /// <summary>
    /// Represent a VNC session started or joined locally.
    /// </summary>
    public class VncLocalSession
    {
        /// <summary>
        /// Registry location of the VNC listening port (server or 
        /// client, depending on the situation).
        /// </summary>
        private const String m_portRegItem = "ListeningPortForVNC";

        /// <summary>
        /// Registry location of the flag set when m_portRegItem
        /// has been written.
        /// </summary>
        private const String m_portRegItemWritten = "ListeningPortForVNCWritten";

        /// <summary>
        /// Reference to the application.
        /// </summary>
        public AppVnc App;

        /// <summary>
        /// Reference to the workspace.
        /// </summary>
        public Workspace Kws;

        /// <summary>
        /// Status of the local session.
        /// </summary>
        public VncSessionStatus Status = VncSessionStatus.Initial;

        /// <summary>
        /// True if this is a server session, false if this is a client session.
        /// </summary>
        public bool ServerSessionFlag = false;

        /// <summary>
        /// True if this is a support session (only for server session).
        /// </summary>
        public bool SupportSessionFlag = false;

        /// <summary>
        /// Handle of the window to share. 0 for the desktop.
        /// </summary>
        public int WindowHandle = 0;

        /// <summary>
        /// Subject of the VNC session.
        /// </summary>
        public String Subject = "";

        /// <summary>
        /// KCD ID of the local session. 0 if none.
        /// </summary>
        public UInt64 SessionID = 0;

        /// <summary>
        /// Tree of completed session IDs sent by the server. We have to cache
        /// those IDs while the session is being established to solve the race 
        /// condition where we receive the "end session" event for the local 
        /// session before the tunnel thread has set the session ID.
        /// </summary>
        public SortedDictionary<UInt64, UInt64> SessionIDTree = new SortedDictionary<UInt64, UInt64>();

        /// <summary>
        /// UUID of the local session.
        public byte[] SessionUuid = Wm.MakeUuid();

        /// <summary>
        /// Creation time.
        /// </summary>
        public DateTime CreationTime = DateTime.Now;

        /// <summary>
        /// Running overlay form.
        /// </summary>
        RunningOverlay Overlay = null;

        /// <summary>
        /// KCD ticket used to start or join a session.
        /// </summary>
        public byte[] Ticket = null;

        /// <summary>
        /// Ticket query issued to the KCD.
        /// </summary>
        public KcdQuery TicketQuery = null;

        /// <summary>
        /// Tunnel between the VNC process and the KCD.
        /// </summary>
        public AnpTunnel Tunnel = null;

        /// <summary>
        /// Thread used to open the tunnel.
        /// </summary>
        public VncTunnelThread TunnelThread = null;

        /// <summary>
        /// Main VNC process.
        /// </summary>
        public KProcess MainProcess = null;

        /// <summary>
        /// Dummy process used to pass parameters to the main VNC process.
        /// MetaVNC stinks.
        /// </summary>
        public KProcess DummyProcess = null;

        /// <summary>
        /// Port used by the VNC to listen for incoming connections.
        /// </summary>
        public int ProcessPort = 0;

        /// <summary>
        /// Timer used to detect when the VNC processes have started and 
        /// stopped. This is poll-based, unfortunately.
        /// </summary>
        public KWakeupTimer Timer = new KWakeupTimer();

        /// <summary>
        /// Number of timer events received.
        /// </summary>
        public int NbTimerEvent = 0;

        /// <summary>
        /// Monitor used to interrupt the session if the user isn't present.
        /// </summary>
        public IInactivityMonitor InactivityMonitor = null;

        public VncLocalSession(AppVnc app)
        {
            App = app;
            Kws = App.Kws;
        }

        /// <summary>
        /// Cancel the session.
        /// </summary>
        public void Cancel()
        {
            HandleSessionTrouble(null);
        }

        /// <summary>
        /// This method should be called when an error occurs in the session.
        /// This is a no-op if the session has completed. 'ex' can be null if
        /// the session ended normally.
        /// </summary>
        public void HandleSessionTrouble(Exception ex)
        {
            if (Status == VncSessionStatus.Completed) return;

            // Determine whether we were starting the session.
            bool startFlag = (Status != VncSessionStatus.Started);

            // Convert the exception to an EAnp exception as needed. If we 
            // failed to start, we always need an exception.
            EAnpException castedEx = null;
            if (ex != null) castedEx = EAnpException.FromException(ex);
            if (castedEx == null && startFlag) castedEx = new EAnpExInterrupted();

            // Terminate this session.
            Terminate();

            // Notify listeners. There are three cases:
            // - The session failed to start.
            // - The session ended normally.
            // - The session eneded abnormally.
            PostLocalVncSessionEvent(startFlag, castedEx);

            Kws.OnStateChange(WmStateChange.Transient);
        }

        /// <summary>
        /// This method is called when the current session step has been
        /// completed. Note the usage pattern: methods called directly by this
        /// method throw their exceptions. Callback methods catch their 
        /// exceptions and report them with HandleSessionTrouble().
        /// </summary>
        public void HandleNextSessionStep()
        {
            try
            {
                // Bail out.
                if (Status == VncSessionStatus.Completed)
                    return;

                // Get a ticket.
                else if (Status == VncSessionStatus.Initial) RequestTicket();

                // Start the main process.
                else if (Status == VncSessionStatus.Ticket)
                    StartMainProcess();

                // Start the dummy process.
                else if (Status == VncSessionStatus.MainProcess && ServerSessionFlag)
                    StartDummyProcess();

                // Open the tunnel.
                else if (Status == VncSessionStatus.MainProcess || Status == VncSessionStatus.DummyProcess)
                    OpenTunnel();

                // Start the session, if possible.
                else if (Status == VncSessionStatus.Tunnel)
                    HandleSessionStart();

                // Houston, we got a problem.
                else Debug.Assert(false);
            }

            catch (Exception ex)
            {
                HandleSessionTrouble(ex);
            }
        }

        /// <summary>
        /// This method should be called when a "VNC session end" event is
        /// received from the KCD.
        /// </summary>
        public void OnSessionEndEventReceived(UInt64 sessionID, EAnpException ex)
        {
            if (Status == VncSessionStatus.Completed) return;
            
            // Check if the local session is being ended.
            else if (Status == VncSessionStatus.Started)
            {
                if (sessionID == SessionID) HandleSessionTrouble(ex);
            }

            // Cache the session ID.
            else
            {
                SessionIDTree[sessionID] = sessionID;
            }
        }

        /// <summary>
        /// Request a ticket to the server.
        /// </summary>
        private void RequestTicket()
        {
            Status = VncSessionStatus.Ticket;
            UInt32 t = ServerSessionFlag ? KAnp.KANP_CMD_VNC_START_TICKET : KAnp.KANP_CMD_VNC_CONNECT_TICKET;
            AnpMsg m = Kws.NewKcdCmd(t);
            if (!ServerSessionFlag) m.AddUInt64(SessionID);
            TicketQuery = Kws.PostKcdCmd(m, OnTicketReply);
        }

        /// <summary>
        /// This method is called when the ticket query has completed.
        /// </summary>
        private void OnTicketReply(KcdQuery query)
        {
            if (Status != VncSessionStatus.Ticket) return;
            TicketQuery = null;

            try
            {
                AnpMsg m = query.Res;
                if (m.Type == KAnp.KANP_RES_VNC_START_TICKET ||
                    m.Type == KAnp.KANP_RES_VNC_CONNECT_TICKET)
                {
                    Ticket = m.Elements[0].Bin;
                    HandleNextSessionStep();
                }

                else throw EAnpException.FromKAnpReply(m);
            }

            catch (Exception ex)
            {
                HandleSessionTrouble(ex);
            }
        }

        /// <summary>
        /// Start the main process.
        /// </summary>
        private void StartMainProcess()
        {
            Status = VncSessionStatus.MainProcess;

            // Handle server session miscellaneous actions.
            if (ServerSessionFlag)
            {
                // Set the support mode.
                SetSupportSessionMode(SupportSessionFlag);

                // If a window is being shared (not the desktop), set it
                // visible and in foreground.
                if (WindowHandle != 0)
                {
                    IntPtr hWnd = new IntPtr(WindowHandle);
                    if (KSyscalls.IsIconic(hWnd))
                        KSyscalls.ShowWindowAsync(hWnd, (int)KSyscalls.WindowStatus.SW_RESTORE);
                    KSyscalls.SetForegroundWindow(hWnd);
                }
            }

            // Remove any indication of previous server's listening port.
            RegistryKey key = null;

            try
            {
                key = KwmReg.GetKwmCURegKey();
                key.DeleteValue(m_portRegItem, false);
                key.DeleteValue(m_portRegItemWritten, false);
            }

            finally
            {
                if (key != null) key.Close();
            }

            // Start the process.
            StartProcess(true);
        }

        /// <summary>
        /// Sets kappserver to allow or deny remote inputs.
        /// </summary>
        private void SetSupportSessionMode(bool supportFlag)
        {
            RegistryKey remoteInputs = null;

            try
            {
                String keyPath = KwmReg.GetVncServerRegKey() + "\\server";
                remoteInputs = Registry.CurrentUser.OpenSubKey(keyPath, true);

                // Create key if it does not exist.
                if (remoteInputs == null)
                {
                    Registry.CurrentUser.CreateSubKey(KwmReg.GetVncServerRegKey());
                    Registry.CurrentUser.CreateSubKey(KwmReg.GetVncServerRegKey() + "\\server");
                    remoteInputs = Registry.CurrentUser.OpenSubKey(keyPath, true);
                    if (remoteInputs == null) throw new Exception("unable to set the Support Mode option");
                }

                remoteInputs.SetValue("InputsEnabled", supportFlag ? 1 : 0, RegistryValueKind.DWord);
            }

            finally
            {
                if (remoteInputs != null) remoteInputs.Close();
            }
        }

        /// <summary>
        /// Start the dummy process.
        /// </summary>
        private void StartDummyProcess()
        {
            Status = VncSessionStatus.DummyProcess;
            StartProcess(false);
        }

        /// <summary>
        /// Start the process specified.
        /// </summary>
        private void StartProcess(bool mainFlag)
        {
            // Kill any lingering process.
            if (mainFlag)
            {
                String lingeringName = ServerSessionFlag ? "kappserver" : "kappviewer";
                foreach (Process l in Process.GetProcessesByName(lingeringName)) l.Kill();
            }

            // Get the executable path and its arguments.
            String path = "\"" + KwmPath.GetKwmInstallationPath() + @"vnc\";
            path += ServerSessionFlag ? "kappserver.exe" : "kappviewer.exe";
            path += "\"";

            String args = "";
            if (ServerSessionFlag && !mainFlag)
            {
                if (WindowHandle == 0) args = " -shareall";
                else args = " -sharehwnd " + WindowHandle;
            }

            else if (!ServerSessionFlag)
            {
                args = " /shared /notoolbar /disableclipboard /encoding tight /compresslevel 9 localhost";
            }

            // Start the process.
            KProcess p = new KProcess(path + args);
            if (mainFlag) MainProcess = p;
            else DummyProcess = p;
            p.CreationFlags = (uint)KSyscalls.CREATION_FLAGS.CREATE_NO_WINDOW;
            p.InheritHandles = false;
            KSyscalls.STARTUPINFO si = new KSyscalls.STARTUPINFO();
            // I don't know why this is set.
            if (ServerSessionFlag) si.dwFlags = 1;
            p.StartupInfo = si;
            p.ProcessEnd += OnProcessEnd;
            p.Start();

            // Poll the process until it starts.
            NbTimerEvent = 0;
            Timer.TimerWakeUpCallback = OnProcessPollEvent;
            Timer.Args = new object[] { p };
            Timer.WakeMeUp(0);
        }

        /// <summary>
        /// This method is called when a VNC process ends.
        /// </summary>
        private void OnProcessEnd(Object sender, ProcEndEventArgs args)
        {
            if (Status == VncSessionStatus.Completed || sender == DummyProcess) return;

            // If we are starting the session, raise an error.
            if (Status != VncSessionStatus.Started)
            {
                String m = "The VNC process exited unexpectedly (code " + args.ExitCode + ")";
                HandleSessionTrouble(new Exception(m));
            }
            
            // Wait a bit to give the KCD a chance to tell us what went wrong,
            // then assume we lost connection to the server and kill the
            // session.
            else
            {
                Timer.TimerWakeUpCallback = OnProcessEndTimeOut;
                Timer.WakeMeUp(1000);
            }
        }

        /// <summary>
        /// This method is called when the process died but the server did not
        /// report an error for it.
        /// </summary>
        private void OnProcessEndTimeOut(Object[] args)
        {
            HandleSessionTrouble(new Exception("VNC process closed unexpectedly"));
        }

        /// <summary>
        /// This method is called when a timer event has been received to check
        /// if the process has started.
        /// </summary>
        private void OnProcessPollEvent(Object[] args)
        {
            if (Status != VncSessionStatus.MainProcess &&
                Status != VncSessionStatus.DummyProcess) return;

            try
            {
                bool foundFlag = false;

                // Check for window handle.
                if (ServerSessionFlag && Status == VncSessionStatus.MainProcess)
                {
                    IntPtr serverHandle = KSyscalls.FindWindow("WinVNC Tray Icon", 0);
                    foundFlag = (serverHandle != IntPtr.Zero);
                }

                // Check the registry value. Get the port.
                else
                {
                    RegistryKey key = KwmReg.GetKwmCURegKey();

                    try
                    {
                        foundFlag = (key.GetValue(m_portRegItemWritten) != null);
                        if (foundFlag) ProcessPort = (int)key.GetValue(m_portRegItem);
                    }

                    finally
                    {
                        if (key != null) key.Close();
                    }
                }

                // Pass to the next step.
                if (foundFlag)
                {
                    HandleNextSessionStep();
                }

                // Poll again later, if possible.
                else
                {
                    NbTimerEvent++;
                    if (NbTimerEvent >= 30) throw new Exception("VNC process does not start");
                    Timer.WakeMeUp(200);
                }
            }

            catch (Exception ex)
            {
                HandleSessionTrouble(ex);
            }
        }

        /// <summary>
        /// Open the tunnel used by the VNC process.
        /// </summary>
        private void OpenTunnel()
        {
            Status = VncSessionStatus.Tunnel;
            TunnelThread = new VncTunnelThread(this);
            TunnelThread.Start();
        }

        /// <summary>
        /// Start the session, if possible.
        /// </summary>
        private void HandleSessionStart()
        {
            // Check if the KCD has already sent us the end session event.
            if (SessionIDTree.ContainsKey(SessionID))
            {
                HandleSessionTrouble(new Exception("the KCD closed the connection unexpectedly"));
                return;
            }

            // Clear the session ID tree.
            SessionIDTree.Clear();

            // Show the overlay window.
            Overlay = new RunningOverlay();
            Overlay.Relink(this);

            // Configure the inactivity monitor to timeout about 10 minutes.
            InactivityMonitor = MonitorCreator.CreateInstance(MonitorType.GlobalHookMonitor);
            InactivityMonitor.MonitorKeyboardEvents = true;
            InactivityMonitor.MonitorMouseEvents = true;
            InactivityMonitor.Interval = 600000;
            InactivityMonitor.Elapsed += delegate(Object sender, ElapsedEventArgs args)
                {
                    KBase.ExecInUI(OnInactivity);
                };
            InactivityMonitor.Enabled = true;

            // Start the session.
            Status = VncSessionStatus.Started;

            // Notify listeners.
            PostLocalVncSessionEvent(true, null);

            Kws.OnStateChange(WmStateChange.Transient);
        }

        /// <summary>
        /// This method is called when the server session times out due to
        /// inactivity.
        /// </summary>
        private void OnInactivity()
        {
            String m = "Your Screen Sharing session has been terminated because it has been idle for too long.";
            HandleSessionTrouble(new Exception(m));
        }

        /// <summary>
        /// Post a LocalVncSession event.
        /// </summary>
        private void PostLocalVncSessionEvent(bool startFlag, EAnpException ex)
        {
            AnpMsg m = Kws.MakeTransientEAnpEvent(EAnpEvt.LocalVncSession);
            m.AddBin(SessionUuid);
            m.AddUInt64(SessionID);
            m.AddUInt32(Convert.ToUInt32(ServerSessionFlag));
            m.AddUInt32(Convert.ToUInt32(startFlag));
            m.AddUInt32(Convert.ToUInt32(ex != null));
            if (ex != null) ex.Serialize(m);
            Kws.PostTransientEAnpEvent(m);
        }

        /// <summary>
        /// Clean up the state when the session has completed to avoid resource
        /// leaks. This object CANNOT be reused for another session since some
        /// recently cancelled threads may still reference the object and try
        /// to modify its state.
        /// </summary>
        private void Terminate()
        {
            Status = VncSessionStatus.Completed;
            AppVnc.SessionPresentFlag = false;
            App.LocalSession = null;

            if (Overlay != null)
            {
                Overlay.Terminate();
                Overlay = null;
            }

            if (TicketQuery != null)
            {
                TicketQuery.Terminate();
                TicketQuery = null;
            }

            if (Tunnel != null)
            {
                Tunnel.Terminate();
                Tunnel = null;
            }

            if (TunnelThread != null)
            {
                TunnelThread.RequestCancellation();
                TunnelThread = null;
            }

            if (MainProcess != null)
            {
                MainProcess.Terminate();
                MainProcess = null;
            }

            if (DummyProcess != null)
            {
                DummyProcess.Terminate();
                DummyProcess = null;
            }

            if (Timer != null)
            {
                Timer.WakeMeUp(-1);
                Timer = null;
            }

            if (InactivityMonitor != null)
            {
                InactivityMonitor.Enabled = false;
                InactivityMonitor.Dispose();
                InactivityMonitor = null;
            }
        }
    }

    /// <summary>
    /// Thread used to open a tunnel between the KCD and the VNC process.
    /// </summary>
    public class VncTunnelThread : KwmTunnelThread
    {
        /// <summary>
        /// Reference to the local session. Some fields of the session are
        /// accessed from this thread without using mutual exclusion. By design
        /// such accesses are safe. This is brittle however; beware on 
        /// refactoring.
        /// </summary>
        private VncLocalSession m_session;

        public VncTunnelThread(VncLocalSession session)
            : base(session.Kws.Cd.Credentials.KcdAddress, 443, "localhost", session.ProcessPort)
        {
            m_session = session;
        }

        /// <summary>
        /// Create an ANP message that can be sent to the server in VNC mode.
        /// </summary>
        public AnpMsg CreateAnpMsg(UInt32 type)
        {
            AnpMsg m = new AnpMsg();
            m.Minor = KAnp.Minor;
            m.Type = type;
            return m;
        }

        /// <summary>
        /// Negociate the role.
        /// </summary>
        private void NegociateRole()
        {
            AnpMsg m = CreateAnpMsg(KAnp.KANP_CMD_MGT_SELECT_ROLE);
            m.AddUInt32(KAnp.KANP_KCD_ROLE_APP_SHARE);
            SendAnpMsg(m);
            m = GetAnpMsg();
            if (m.Type == KAnp.KANP_RES_FAIL) throw EAnpException.FromKAnpReply(m);
            if (m.Type != KAnp.KANP_RES_OK) throw new Exception("expected RES_OK in role negociation");
        }

        /// <summary>
        /// Negociate the session.
        /// </summary>
        private void NegociateSession()
        {
            AnpMsg m = null;

            if (m_session.ServerSessionFlag)
            {
                m = CreateAnpMsg(KAnp.KANP_CMD_VNC_START_SESSION);
                m.AddBin(m_session.Ticket);
                m.AddString(m_session.Subject);
            }

            else
            {
                m = CreateAnpMsg(KAnp.KANP_CMD_VNC_CONNECT_SESSION);
                m.AddBin(m_session.Ticket);
            }

            SendAnpMsg(m);
            m = GetAnpMsg();

            if (m_session.ServerSessionFlag)
            {
                if (m.Type != KAnp.KANP_RES_VNC_START_SESSION) throw EAnpException.FromKAnpReply(m);
                m_session.SessionID = m.Elements[0].UInt64;
            }

            else
            {
                if (m.Type != KAnp.KANP_RES_OK) throw EAnpException.FromKAnpReply(m);
            }
        }

        protected override void OnTunnelConnected()
        {
            NegociateRole();
            NegociateSession();

            // Disconnect the tunnel. This will make it reconnect to the
            // local VNC process.
            InternalAnpTunnel.Disconnect();
        }

        protected override void OnCompletion()
        {
            // Failure.
            if (Status == WorkerStatus.Cancelled ||
                Status == WorkerStatus.Failed ||
                m_session.Status != VncSessionStatus.Tunnel)
            {
                // Kill the tunnel.
                if (InternalAnpTunnel != null) InternalAnpTunnel.Terminate();

                // Notify the session.
                if (m_session.Status == VncSessionStatus.Tunnel)
                {
                    Debug.Assert(Status != WorkerStatus.Cancelled);
                    m_session.HandleSessionTrouble(FailException);
                }
            }

            // Success.
            else
            {
                // Transfer ownership of the tunnel.
                m_session.Tunnel = InternalAnpTunnel;
                InternalAnpTunnel = null;

                // Handle the next step.
                m_session.HandleNextSessionStep();
            }
        }
    }
}
