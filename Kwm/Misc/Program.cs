using kcslib;
using kwmlib;
using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using System.Threading;
using Microsoft.Win32;
using CodePoints;

namespace kwm
{
    // A short story about single instance applications.
    //
    // For the last 15 years, programmers have struggled to make single instance
    // applications. The concept is easy: make sure the bloody app is running
    // once and only once per user. It gets muddier when you consider multiple
    // sessions and users running into each other sessions. It gets harder if 
    // your application doesn't have a main window. It get absolutely hairy if
    // you're trying to pass information from a new application instance to an 
    // old one.
    // 
    // There are lots of incorrect implementations out there. Stuff that 
    // depends on FindWindowEx(), GetProcessesByName(), 
    // Process.MainWindowHandle, placing the main window offscreen, using the
    // VB single application kludge, etc. 
    //
    // I've spent 9 hours reviewing that garbage. There is not a single 
    // solution that works reliably if you want to pass information between
    // instances. The only thing that seems correct at the first glance is 
    // using a mutex, but that doesn't address the problem of passing 
    // information between the instances. 
    //
    // Some people suggested to use named pipes, sockets, remoting, 
    // SendMessage and other APIs to achieve this. All those methods suffer 
    // from race conditions and are pretty complex to setup.
    //
    // Microsoft could have developed a reliable API to help developers.
    // Instead, they provided incorrect information and flawed implementations.
    //
    // So, here we go with our own broken scheme. We use GetProcessesByName()
    // to identify other instances of the KWM. This is a race condition and
    // this is also unreliable for other reasons. We communicate between 
    // instances with SendMessage(). This is a race condition. 
    //
    // We do not use Process.MainWindowHandle since it is null if the 
    // application doesn't have a visible window, which is our case.
    // Instead, we create a message-only window. As far as I can tell
    // there is no race condition with this part. We pass the handle to this
    // window through the registry. This is a race condition.

    /// <summary>
    /// State of the other KWM process.
    /// </summary>
    public enum WmOtherProcessState
    {
        /// <summary>
        /// No conflicting KWM process.
        /// </summary>
        None,

        /// <summary>
        /// A KWM process started by our user is running in our session.
        /// </summary>
        OurInCurrentSession,

        /// <summary>
        /// A KWM process started by our user is running in another session.
        /// </summary>
        OurInOtherSession,

        /// <summary>
        /// A KWM process not started by our user is running in our session.
        /// </summary>
        NotOurInCurrentSession
    }

    /// <summary>
    /// State of the other KWM process.
    /// </summary>
    public class WmOtherProcess
    {
        /// <summary>
        /// State of the process.
        /// </summary>
        public WmOtherProcessState State = WmOtherProcessState.None;

        /// <summary>
        /// Other process, if any.
        /// </summary>
        public Process OtherProcess = null;

        /// <summary>
        /// Handle of the message window of the other process. 
        /// </summary>
        public IntPtr OtherWindowHandle = IntPtr.Zero;

        /// <summary>
        /// Obtain the information about the KWM instance that is related to 
        /// our instance, if any.
        /// </summary>
        public void FindOtherProcess()
        {
            State = WmOtherProcessState.None;
            OtherProcess = null;
            OtherWindowHandle = IntPtr.Zero;

            Process currentProcess = Process.GetCurrentProcess();
            String procName = currentProcess.ProcessName;
            WindowsIdentity userWI = WindowsIdentity.GetCurrent();

            // Try to find a matching process.
            foreach (Process p in Process.GetProcessesByName(procName))
            {
                String stringSID = KSyscalls.GetProcessSid(p);

                // Ignore our current process.
                if (p.Id == currentProcess.Id) continue;
                bool InOurSessionFlag = currentProcess.SessionId == p.SessionId;

                // This process has been started by the current user.
                if (String.Compare(stringSID, userWI.User.Value, true) == 0)
                {
                    if (InOurSessionFlag) State = WmOtherProcessState.OurInCurrentSession;
                    else State = WmOtherProcessState.OurInOtherSession;
                    OtherProcess = p;
                    break;
                }

                if (InOurSessionFlag)
                {
                    State = WmOtherProcessState.NotOurInCurrentSession;
                    OtherProcess = p;
                    break;
                }
            }

            // Process found. Get the main window handle.
            if (OtherProcess != null)
                OtherWindowHandle = Program.GetOtherKwmHandle();
        }
    }

    /// <summary>
    /// Window receiving the messages sent by the other processes.
    /// </summary>
    public class WmMsgWindow : NativeWindow
    {
        public WmMsgWindow()
        {
            // Make the window a message-only window.
            CreateParams cp = new CreateParams();
            cp.Parent = new IntPtr(-3);
            CreateHandle(cp);
        }

        /// <summary>
        /// Called to process the messages received by this window.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == KSyscalls.WM_COPYDATA)
            {
                KSyscalls.COPYDATASTRUCT cds = (KSyscalls.COPYDATASTRUCT)m.GetLParam(typeof(KSyscalls.COPYDATASTRUCT));
                int msgID = cds.dwData.ToInt32();

                // This is for us.
                if (msgID == Program.ImportKwsMsgID || msgID == Program.ForegroundMsgID)
                {
                    // Retrieve the path argument. Clone the string to avoid
                    // any potential trouble.
                    String path = cds.lpData.Clone() as String;

                    if (msgID == Program.ForegroundMsgID)
                    {
                        // FIXME.
                        KLogging.Log("Received request to put process in the foreground.");
                    }

                    // This is our import workspace credentials message.
                    else if (msgID == Program.ImportKwsMsgID)
                    {
                        KLogging.Log("Received request to import workspace from window procedure.");

                        // Perform the import.
                        Program.ImportKwsList(path);
                    }
                }
            }

            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// Represent the KWM program.
    /// </summary>
    static class Program
    {
        /// <summary>
        /// ID of the message sent to import a workspace. Using a high ID in
        /// case the message lands in the message queue of the wrong process. 
        /// </summary>
        public const int ImportKwsMsgID = 7775632;

        /// <summary>
        /// ID of the message sent to put the KWM/ET in the foreground.
        /// </summary>
        public const int ForegroundMsgID = 7775633;

        /// <summary>
        /// Path to the directory where the workspaces will be exported.
        /// </summary>
        public static String ExportKwsPath = "";

        /// <summary>
        /// Path to the workspace credentials file to import.
        /// </summary>
        public static String ImportKwsPath = "";

        /// <summary>
        /// Window receiving the messages sent by other processes.
        /// </summary>
        public static WmMsgWindow MsgWindow = null;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        private static void Main(String[] args)
        {
            // Initialize our application.
            AppInit();

            // Parse the command line.
            ParseCmdLine(args);

            // We can catch fatal errors here without risking an infinite KWM
            // spawn loop.
            WmUi.FatalErrorMsgOKFlag = true;

            // Execute the bootstrap method when the message loop is running.
            KBase.ExecInUI(new KBase.EmptyDelegate(Bootstrap));
            
            // Run the message loop.
            Application.Run();
        }

        /// <summary>
        /// Request the application to quit as soon as possible.
        /// </summary>
        public static void RequestAppExit()
        {
            Application.Exit();
        }

        /// <summary>
        /// Initialize the application on startup.
        /// </summary>
        private static void AppInit()
        {
            // Somehow this call doesn't make the output visible in cygwin 
            // bash. It works for cmd.exe.
            KSyscalls.AttachConsole(KSyscalls.ATTACH_PARENT_PROCESS);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MsgWindow = new WmMsgWindow();
            KBase.InvokeUiControl = new Control();
            KBase.InvokeUiControl.CreateControl();
            KBase.HandleErrorCallback = WmUi.HandleError;
            Application.ThreadException += HandleUnhandledException;
            KwmCfg.Cur = KwmCfg.Spawn();
            KLogging.Logger = KwmLogger.Logger;
            KwmLogger.SetLoggingLevel(KwmCfg.Cur.KwmDebuggingFlag ? KwmLoggingLevel.Normal : KwmLoggingLevel.Debug);
        }

        /// <summary>
        /// Handle an unhandled exception escaping from a thread, including the
        /// main thread. Supposedly. That's what they said about
        /// AppDomain.CurrentDomain.UnhandledException and it just did so in
        /// debug mode.
        /// </summary>
        private static void HandleUnhandledException(Object sender, ThreadExceptionEventArgs args)
        {
            KBase.HandleException(args.Exception, true);
        }

        /// <summary>
        /// Parse the command line and exit the process as needed.
        /// </summary>
        private static void ParseCmdLine(string[] args)
        {
            try
            {
                int c = 0;
                while ((c = GetOpt.GetOptions(args, "e:i:M:S:")) != -1)
                {
                    switch ((char)c)
                    {
                        // Export switch.
                        case 'e':
                            ExportKwsPath = GetOpt.Text;
                            if (ExportKwsPath == "" || ExportKwsPath == null)
                                throw new Exception("empty export path");
                            break;

                        // Import switch.
                        case 'i':
                            ImportKwsPath = GetOpt.Text;
                            if (ImportKwsPath == "" || ImportKwsPath == null)
                                throw new Exception("empty import path");
                            break;

                        // Fatal error message switch.
                        case 'M':
                            WmUi.TellUser(GetOpt.Text, KwmStrings.Kwm + " fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            Environment.Exit(0);
                            break;

                        case '?':
                        case ':':
                        default:
                            throw new Exception("invalid option");
                    }
                }
            }

            catch (Exception ex)
            {
                Console.WriteLine("Option error: " + ex.Message);
                usage();
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// Display the program usage.
        /// </summary>
        public static void usage()
        {
            Console.WriteLine("Usage: \nkwm.exe [(-e]) <path-to-file>] [-M <fatal-message>]");
        }

        /// <summary>
        /// This method is executed when the application has started and the
        /// message loop is running.
        /// </summary>
        private static void Bootstrap()
        {
            // Determine the other process state.
            WmOtherProcess other = new WmOtherProcess();
            other.FindOtherProcess();

            // Another KWM we own is running in our session.
            if (other.State == WmOtherProcessState.OurInCurrentSession)
            {
                // Send the other KWM a message to import the credentials.
                if (ImportKwsPath != "") SendMsgToOtherKwm(other, ImportKwsMsgID, ImportKwsPath);

                // Show the instance of the other KWM.
                SendMsgToOtherKwm(other, ForegroundMsgID, "");

                // We're done.
                RequestAppExit();
            }

            // Warn the user.
            else if (other.State == WmOtherProcessState.NotOurInCurrentSession ||
                     other.State == WmOtherProcessState.OurInOtherSession)
            {
                String error = (other.State == WmOtherProcessState.NotOurInCurrentSession) ?
                    "A " + KwmStrings.Kwm + " started by another user is already running." :
                    "A " + KwmStrings.Kwm + " started by another user is already running.";
                WmUi.TellUser(error, "Cannot start " + KwmStrings.Kwm, MessageBoxButtons.OK, MessageBoxIcon.Error);
                RequestAppExit();
            }

            // Enter the main mode.
            else
            {
                try
                {
                    if (!EnterMainMode()) RequestAppExit();
                }

                catch (Exception ex)
                {
                    KBase.HandleException(ex, true);
                }
            }
        }

        /// <summary>
        /// Enter the main mode of the KWM. Return true if the application
        /// must continue.
        /// </summary>
        private static bool EnterMainMode()
        {
            // Perform early linking.
            Wm.LocalDbBroker.Relink(Wm.LocalDb);

            // FIXME.
#if true
            // Open a temporary console.
            ConsoleWindow foo = new ConsoleWindow();
            foo.Show();
            foo.OnConsoleClosing += delegate(Object sender, EventArgs args)
            {
                WmSm.RequestStop();
            };

            // Create an empty database.
            Wm.LocalDb.DeleteDb();
            Wm.LocalDb.OpenOrCreateDb(KwmPath.GetKwmDbPath());
            Wm.LocalDbBroker.InitDb();
            WmDeserializer ds = new WmDeserializer();
            ds.Deserialize();
            Debug.Assert(ds.Ex == null);

#else
            // Open or create the database.
            Wm.LocalDb.OpenOrCreateDb(KwmPath.GetKwmDbPath());
            Wm.LocalDbBroker.InitDb();

            // Try to deserialize.
            WmDeserializer ds = new WmDeserializer();
            ds.Deserialize();

            // The deserialization failed.
            if (ds.Ex != null)
            {
                // If the user doesn't want to recover, bail out.
                if (!TellUserAboutDsrFailure()) return false;

                // Backup, delete and recreate a database.
                BackupDb();
                Wm.LocalDb.DeleteDb();
                Wm.LocalDb.OpenOrCreateDb(KwmPath.GetKwmDbPath());
                Wm.LocalDbBroker.InitDb();

                // Retry to deserialize.
                ds = new WmDeserializer();
                ds.Deserialize();
                if (ds.Ex != null) throw ds.Ex;

                // Set the next internal workspace ID to a high value based on
                // the date to avoid conflicts with old KFS directories.
                ds.WmCd.NextKwsInternalID = (UInt64)(DateTime.Now - new DateTime(2010, 1, 1)).TotalSeconds;
            }
#endif

            // Relink the workspace manager object graphs.
            Wm.Relink(ds);

            // Open the lingering database transaction.
            Wm.LocalDb.BeginTransaction();

            // Serialize the WM state that has changed.
            Wm.Serialize();

            // Export the workspaces, then exit.
            if (ExportKwsPath != "")
            {
                WmKwsImportExport.ExportKws(ExportKwsPath, 0);
                return false;
            }

            // Set the handle to the message window.
            SetKwmHandle(MsgWindow.Handle);

            // Pass the hand to the WM state machine.
            WmSm.RequestStart();

            return true;
        }

        /// <summary>
        /// Import the workspace list specified.
        /// </summary>
        public static void ImportKwsList(String path)
        {
            try
            {
                WmKwsImportExport.ImportKwsListFromFile(path);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex);
            }
        }

        /// <summary>
        /// Return true if the user wants to recover from the deserialization
        /// failure.
        /// </summary>
        private static bool TellUserAboutDsrFailure()
        {
            String msg = "The " + KwmStrings.Kwm + " data is corrupted. " +
                         "Do you want to delete the corrupted data? " +
                         "Warning: you will lose all your " + KwmStrings.Kwses + "!";
            return WmUi.TellUser(msg, KwmStrings.Kwm + " data corrupted", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        /// <summary>
        /// Backup the database, if it exists.
        /// </summary>
        private static void BackupDb()
        {
            String srcPath = KwmPath.GetKwmDbPath();
            String dstPath = srcPath + ".backup";
            if (File.Exists(srcPath)) File.Copy(srcPath, dstPath, true);
        }

        /// <summary>
        /// Send the message specified to the other KWM specified, if possible.
        /// </summary>
        private static void SendMsgToOtherKwm(WmOtherProcess other, UInt32 msgID, String path)
        {
            try
            {
                // Wait 10 seconds for the other process to finish initializing,
                // then send the message.
                if (other.OtherProcess != null &&
                    other.OtherWindowHandle != IntPtr.Zero &&
                    other.OtherProcess.WaitForInputIdle(10 * 1000))
                {
                    KSyscalls.COPYDATASTRUCT cds;
                    cds.dwData = new IntPtr(msgID);
                    cds.lpData = path;
                    cds.cbData = Encoding.Default.GetBytes(path).Length + 1;
                    KSyscalls.SendMessage(other.OtherWindowHandle.ToInt32(), KSyscalls.WM_COPYDATA, 0, ref cds);
                }
            }

            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Set the handle to the other KWM process.
        /// </summary>
        private static void SetKwmHandle(IntPtr handle)
        {
            RegistryKey regKey = null;

            try
            {
                regKey = KwmReg.GetKwmCURegKey();
                regKey.SetValue("kwmWindowHandle", handle.ToString());
            }

            finally
            {
                if (regKey != null) regKey.Close();
            }
        }

        /// <summary>
        /// Return the handle to the other KWM process, or IntPtr.Zero if none.
        /// </summary>
        public static IntPtr GetOtherKwmHandle()
        {
            RegistryKey regKey = null;

            try
            {
                regKey = KwmReg.GetKwmCURegKey();
                int handle = Int32.Parse((String)regKey.GetValue("kwmWindowHandle", "0"));
                if (handle != 0) return new IntPtr(handle);
                else return IntPtr.Zero;
            }

            finally
            {
                if (regKey != null) regKey.Close();
            }
        }
    }
}