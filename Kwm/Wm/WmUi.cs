using kcslib;
using kwmlib;
using System;
using System.Windows.Forms;
using System.Diagnostics;
using System.Collections.Generic;

namespace kwm
{
    /// <summary>
    /// Workspace Manager UI functionalities.
    /// </summary>
    public static class WmUi
    {
        /// <summary>
        /// Number of times the UI has been reentered to display a dialog.
        /// We use this value to avoid spawning dialogs on top of dialogs.
        /// </summary>
        public static UInt32 UiEntryCount = 0;

        /// <summary>
        /// This flag indicates whether it is safe to display a fatal error
        /// message. This is false on startup to prevent recursive fatal 
        /// error spawners.
        /// </summary>
        public static bool FatalErrorMsgOKFlag = false;

        /// <summary>
        /// True if a fatal error is being handled.
        /// </summary>
        private static volatile bool m_fatalErrorCaughtFlag = false;

        /// <summary>
        /// Return true if the UI can be entered safely.
        /// </summary>
        public static bool IsUiStateOK()
        {
            return (Wm.MainStatus != WmMainStatus.Stopped);
        }

        /// <summary>
        /// Ensure that the caller is executing in UI context.
        /// </summary>
        public static void EnsureNoInvokeRequired()
        {
            if (IsUiStateOK() && !KBase.IsInUi())
                HandleError("Executing outside UI context", true);
        }

        /// <summary>
        /// This method should be called when the UI is reentered.
        /// </summary>
        public static void OnUiEntry()
        {
            UiEntryCount++;
        }

        /// <summary>
        /// This method should be called when the UI is exited.
        /// </summary>
        public static void OnUiExit()
        {
            Debug.Assert(UiEntryCount > 0);
            UiEntryCount--;
            if (UiEntryCount == 0) WmSm.HandleUiExit();
        }

        /// <summary>
        /// Don't ask, don't tell. Close your eyes. Go away. Life is too short
        /// to dick around with this crap.
        /// </summary>
        private static String EscapeArgForFatalError(String insanity)
        {
            insanity = insanity.Replace("\"", "'");
            insanity = insanity.Replace("\\", "\"\\\\\"");
            return insanity;
        }

        /// <summary>
        /// Display an error message to the user and exit the application if 
        /// required.
        /// </summary>
        public static void HandleError(String errorMessage, bool fatalFlag)
        {
            string msg = "An error has been detected." +
                                     Environment.NewLine + Environment.NewLine +
                                     errorMessage +
                                     Environment.NewLine + Environment.NewLine;
            if (fatalFlag)
            {
                msg += "Please restart your " + KwmStrings.Kwm;
            }
            else
            {
                msg += "Please contact your technical support for further information.";
            }

            // Transient error. Queue the message to be displayed.
            if (!fatalFlag)
            {
                WmErrorMsg em = new WmErrorMsg();
                em.Ex = new Exception(errorMessage);
                WmErrorGer ger = new WmErrorGer(em);
                ger.Queue();
                return;
            }

            // The .Net framework is critically brain damaged when it comes to
            // handling fatal errors. There are basically two choices: exit
            // the process right away or try to get the application to display
            // the error and quit.
            //
            // The former choice is sane; there is no risk of further data
            // corruption if the process exits right away. The lack of any
            // error message is problematic however. We work around this by
            // spawning an external program, if possible, to report the error
            // before exiting the process.
            //
            // The second choice cannot be done sanely. If a MessageBox()
            // call is made immediately when the error is detected, then
            // the UI will be reentered and the damage may spread further.
            // Typically this causes multiple fatal error messages to
            // appear. After some investigation, I believe this is impossible
            // to prevent. The best available thing is ThreadAbortException,
            // which has weird semantics and is considered deprecated and
            // doesn't do the right thing in worker threads.

            // Exit right away.
            if (!FatalErrorMsgOKFlag || m_fatalErrorCaughtFlag)
                Environment.Exit(1);

            // We have caught a fatal error. Prevent the other threads from
            // spawning a fatal error. There is an inherent race condition
            // here which is best left alone; mutexes have no business here.
            m_fatalErrorCaughtFlag = true;

            // Spawn a program to display the message.
            try
            {
                String startupLine = '"' + Application.ExecutablePath + '"' + " ";
                startupLine += "\"-M\" \"" + EscapeArgForFatalError(msg) + "\"";
                KProcess p = new KProcess(startupLine);
                p.InheritHandles = false;
                p.Start();
            }

            // Ignore all exceptions.
            catch (Exception)
            {
            }

            // Get out.
            Environment.Exit(1);
        }

        /// <summary>
        /// Show a message to the user synchronously with a dialog box.
        /// </summary>
        public static DialogResult TellUser(String _message, String _title,
                                            MessageBoxButtons _buttons, MessageBoxIcon _icon)
        {
            try
            {
                EnsureNoInvokeRequired();

                // The UI is in a bad state. Do not reference the main form or the
                // UI broker.
                if (!IsUiStateOK()) return MessageBox.Show(_message, _title, _buttons, _icon);

                // Display the message box properly.
                OnUiEntry();
                DialogResult res = MessageBox.Show(_message, _title, _buttons, _icon);
                OnUiExit();
                return res;
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
                return DialogResult.Cancel;
            }
        }
    }

    /// <summary>
    /// Instances of this class are executed asynchronously in the context of
    /// the UI, one at a time. The instances may reenter the UI.
    /// </summary>
    public abstract class WmGuiExecRequest
    {
        /// <summary>
        /// List of pending GUI execution request.
        /// </summary>
        public static Queue<WmGuiExecRequest> GerQueue = new Queue<WmGuiExecRequest>();

        /// <summary>
        /// Queue the request to be executed.
        /// </summary>
        public void Queue()
        {
            KBase.ExecInUI(InternalQueue);
        }

        /// <summary>
        /// Queue and execute the request.
        /// </summary>
        private void InternalQueue()
        {
            // Queue the request.
            GerQueue.Enqueue(this);

            // Bail out, there is another request already executing.
            if (GerQueue.Count > 1) return;

            // Increment the UI entry count.
            WmUi.OnUiEntry();

            // Execute all the pending requests.
            while (GerQueue.Count > 0)
            {
                GerQueue.Peek().Run();
                GerQueue.Dequeue();
            }

            // Decrement the UI entry count.
            WmUi.OnUiExit();
        }

        /// <summary>
        /// This method is called when the request is ready to be executed.
        /// </summary>
        public abstract void Run();
    }

    /// <summary>
    /// Represent an error message to be displayed to the user.
    /// </summary>
    public class WmErrorMsg
    {
        /// <summary>
        /// Exception describing the error.
        /// </summary>
        public Exception Ex;
    }

    /// <summary>
    /// Used by the WM to display an error message in the UI asynchronously.
    /// </summary>
    public class WmErrorGer : WmGuiExecRequest
    {
        public WmErrorMsg ErrorMsg;

        public WmErrorGer(WmErrorMsg errorMsg)
        {
            ErrorMsg = errorMsg;
        }

        public override void Run()
        {
            WmUi.TellUser(ErrorMsg.Ex.Message, KwmStrings.Kwm, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
