using kcslib;
using kwmlib;
using System;
using System.Diagnostics;

namespace kwm
{
    /// <summary>
    /// Outcome of the last login attempt of a workspace.
    /// </summary>
    public enum KwsLoginResult
    {
        /// <summary>
        /// No attempt has been made yet.
        /// </summary>
        None,

        /// <summary>
        /// The credentials have been accepted by the KCD.
        /// </summary>
        Accepted,

        /// <summary>
        /// The security credentials were refused.
        /// </summary>
        BadSecurityCreds,

        /// <summary>
        /// Special case of BadSecurityCreds: the user must provide a password.
        /// </summary>
        PwdRequired,

        /// <summary>
        /// The workspace ID is invalid.
        /// </summary>
        BadKwsID,

        /// <summary>
        /// The email ID is invalid or it has been purged from the database.
        /// </summary>
        BadEmailID,

        /// <summary>
        /// The workspace has been deleted.
        /// </summary>
        DeletedKws,

        /// <summary>
        /// The user account has been locked.
        /// </summary>
        AccountLocked,

        /// <summary>
        /// The credentials are accepted but the login failed since the 
        /// information about the last event received is invalid, probably
        /// because the server has lost some events. All events must be 
        /// refetched from the server.
        /// </summary>
        OOS,

        /// <summary>
        /// No ticket could be obtained from the KPS when attempting to login
        /// using a ticket.
        /// </summary>
        CannotGetTicket,

        /// <summary>
        /// A miscellaneous KCD error occurred.
        /// </summary>
        MiscKcdError,

        /// <summary>
        /// The user has been banned.
        /// </summary>
        Banned
    }

    /// <summary>
    /// Type of workspace login being performed.
    /// </summary>
    public enum KwsLoginType
    {
        /// <summary>
        /// Only the cached login step can be performed.
        /// </summary>
        Cached,

        /// <summary>
        /// All the login steps may be performed.
        /// </summary>
        All
    }

    /// <summary>
    /// Workspace login step being performed.
    /// </summary>
    public enum KwsLoginStep
    {
        /// <summary>
        /// No steps yet.
        /// </summary>
        None,

        /// <summary>
        /// Login using the cached workspace credentials.
        /// </summary>
        Cached,

        /// <summary>
        /// Login using a ticket obtained from the KPS.
        /// </summary>
        Ticket,

        /// <summary>
        /// Login using a password obtained by prompting the user.
        /// </summary>
        Pwd
    }

    /// <summary>
    /// Information contained in a KANP_RES_KWS_CONNECT_KWS reply.
    /// </summary>
    public class KwsConnectRes
    {
        public UInt32 Code;
        public String ErrMsg;
        public UInt32 UserID;
        public String EmailID;
        public UInt64 LoginLatestEventID;
        public bool SecureFlag;
        public bool PwdOnKcdFlag;
        public String KwmoAddress;

        public KwsConnectRes(AnpMsg res)
        {
            Code = res.Elements[0].UInt32;
            ErrMsg = res.Elements[1].String;
            UserID = res.Elements[2].UInt32;
            EmailID = res.Elements[3].String;
            LoginLatestEventID = res.Elements[4].UInt64;
            SecureFlag = (res.Elements[5].UInt32 > 0);
            PwdOnKcdFlag = (res.Elements[6].UInt32 > 0);
            KwmoAddress = res.Elements[7].String;
        }
    }

    /// <summary>
    /// Workspace KCD login handler. 
    /// </summary>
    public class KwsKcdLoginHandler
    {
        /// <summary>
        /// Fired when the password set with SetLoginPwd() is refused. 
        /// </summary>
        public event KBase.EmptyDelegate OnSetLoginPwdRefused;

        /// <summary>
        /// Reference to the workspace.
        /// </summary>
        private Workspace m_kws;

        /// <summary>
        /// Reference to the KCD state.
        /// </summary>
        private KwsKcdState m_ks;

        /// <summary>
        /// Current login step being performed.
        /// </summary>
        private KwsLoginStep m_currentStep = KwsLoginStep.None;

        /// <summary>
        /// Login type.
        /// </summary>
        private KwsLoginType m_loginType = KwsLoginType.All;

        /// <summary>
        /// Ticket query currently under way, if any.
        /// </summary>
        private WmLoginTicketQuery m_ticketQuery = null;

        public void Relink(Workspace kws)
        {
            m_kws = kws;
            m_ks = kws.Cd.KcdState;
        }

        /// <summary>
        /// Translate a KCD login status code to its KwsLoginResult equivalent.
        /// </summary>
        public KwsLoginResult TranslateKcdLoginStatusCode(UInt32 code)
        {
            if (code == KAnp.KANP_KWS_LOGIN_OK) return KwsLoginResult.Accepted;
            if (code == KAnp.KANP_KWS_LOGIN_BAD_PWD_OR_TICKET) return KwsLoginResult.BadSecurityCreds;
            if (code == KAnp.KANP_KWS_LOGIN_OOS) return KwsLoginResult.OOS;
            if (code == KAnp.KANP_KWS_LOGIN_BAD_KWS_ID) return KwsLoginResult.BadKwsID;
            if (code == KAnp.KANP_KWS_LOGIN_BAD_EMAIL_ID) return KwsLoginResult.BadEmailID;
            if (code == KAnp.KANP_KWS_LOGIN_DELETED_KWS) return KwsLoginResult.DeletedKws;
            if (code == KAnp.KANP_KWS_LOGIN_ACCOUNT_LOCKED) return KwsLoginResult.AccountLocked;
            if (code == KAnp.KANP_KWS_LOGIN_BANNED) return KwsLoginResult.Banned;
            return KwsLoginResult.MiscKcdError;
        }

        /// <summary>
        /// This method is called by the workspace state machine to indicate
        /// that the workspace is logged out.
        /// </summary>
        public void ResetOnLogout()
        {
            m_currentStep = KwsLoginStep.None;
            m_ks.ResetOnLogout();
            ClearTicketQuery();
        }

        /// <summary>
        /// Set the login type. This method shouldn't be called outside the
        /// state machine.
        /// </summary>
        public void SetLoginType(KwsLoginType type)
        {
            m_loginType = type;
        }

        /// <summary>
        /// Set the login password. Return true if a login attempt will be made
        /// immmediately with the password provided.
        /// </summary>
        public bool SetLoginPwd(String pwd)
        {
            // Update the password.
            m_kws.Cd.Credentials.Pwd = pwd;
            m_kws.OnStateChange(WmStateChange.Internal);

            // We were not expecting a password.
            if (m_currentStep != KwsLoginStep.Pwd) return false;

            // Send the login command.
            SendLoginCommand();
            return true;
        }

        /// <summary>
        /// Called by the workspace state machine to log in the workspace.
        /// </summary>
        public void PerformLogin()
        {
            Debug.Assert(m_currentStep == KwsLoginStep.None);

            // We perform the cached step if explicitly required, if the 
            // workspace is open, if we have a ticket and a password, or if we
            // cannot login on the KPS. The latter condition is necessary since
            // we have to try to login once to determine whether a password is
            // available on the KCD. Otherwise, we perform the ticket step
            // directly.
            KwsCredentials creds = m_kws.Cd.Credentials;
            bool cachedFlag = (m_loginType == KwsLoginType.Cached ||
                               !creds.SecureFlag ||
                               creds.Ticket != null ||
                               creds.Pwd != "" ||
                               !KwmCfg.Cur.CanLoginOnKps());

            if (cachedFlag) HandleCachedLoginStep();
            else HandleTicketLoginStep();
        }

        /// <summary>
        /// Called by the workspace state machine to log out of the workspace
        /// normally.
        /// </summary>
        public void PerformLogout()
        {
            // Send the logout command.
            m_kws.PostKcdCmd(m_kws.NewKcdCmd(KAnp.KANP_CMD_KWS_DISCONNECT_KWS), HandleDisconnectKwsReply);
        }

        /// <summary>
        /// Cancel and clear the ticket query, if required.
        /// </summary>
        private void ClearTicketQuery()
        {
            if (m_ticketQuery != null)
            {
                m_ticketQuery.Cancel();
                m_ticketQuery = null;
            }
        }

        /// <summary>
        /// Handle a login failure. The login result code and string are set,
        /// the workspace is set dirty and the state machine is notified.
        /// </summary>
        private void HandleLoginFailure(KwsLoginResult res, Exception ex)
        {
            m_ks.LoginResult = res;
            m_ks.LoginResultString = ex.Message;
            m_kws.OnStateChange(WmStateChange.Permanent);
            m_kws.Sm.HandleLoginFailure(ex);
        }

        /// <summary>
        /// Send the cached credentials to the KCD.
        /// </summary>
        private void HandleCachedLoginStep()
        {
            m_currentStep = KwsLoginStep.Cached;
            SendLoginCommand();
        }

        /// <summary>
        /// Obtain a login ticket and login with it.
        /// </summary>
        private void HandleTicketLoginStep()
        {
            m_currentStep = KwsLoginStep.Ticket;
            m_ticketQuery = new WmLoginTicketQuery();
            m_ticketQuery.Submit(Wm.KmodBroker, KwmCfg.Cur, HandleTicketLoginResult);
        }

        /// <summary>
        /// Notify the client that we need a password.
        /// </summary>
        private void HandlePwdLoginStep()
        {
            m_currentStep = KwsLoginStep.Pwd;
            m_ks.PwdRequiredFlag = true;
            m_kws.OnStateChange(WmStateChange.Transient);
        }

        /// <summary>
        /// Handle the results of the ticket query.
        /// </summary>
        private void HandleTicketLoginResult(WmLoginTicketQuery query)
        {
            // Clear the query.
            Debug.Assert(m_ticketQuery == query);
            m_ticketQuery = null;

            // Update the registry, if required.
            query.UpdateRegistry();

            // The query failed.
            if (query.Res == WmLoginTicketQueryRes.InvalidCfg ||
                query.Res == WmLoginTicketQueryRes.MiscError)
            {
                HandleLoginFailure(KwsLoginResult.CannotGetTicket, new Exception("cannot obtain ticket: " + query.OutDesc));
            }

            // The query succeeded.
            else
            {
                // Update the credentials.
                m_kws.Cd.Credentials.Ticket = query.Ticket.BinaryTicket;
                m_kws.OnStateChange(WmStateChange.Permanent);

                // Send the login command.
                SendLoginCommand();
            }
        }

        /// <summary>
        /// Send a login command to the KCD.
        /// </summary>
        private void SendLoginCommand()
        {
            AnpMsg msg = m_kws.NewKcdCmd(KAnp.KANP_CMD_KWS_CONNECT_KWS);

            // Add the delete workspace flag.
            if (m_kws.Kcd.MinorVersion >= 4)
            {
                msg.AddUInt32(Convert.ToUInt32(m_kws.Cd.CurrentTask == KwsTask.DeleteRemotely));
            }

            // Add the last event information.
            AnpMsg lastEvent = m_kws.GetLastKAnpEventInDb();

            if (lastEvent == null)
            {
                msg.AddUInt64(0);
                msg.AddUInt64(0);
            }

            else
            {
                msg.AddUInt64(lastEvent.ID);
                msg.AddUInt64(lastEvent.Elements[1].UInt64);
            }

            // Add the credential information.
            msg.AddUInt32(m_kws.Cd.Credentials.UserID);
            msg.AddString(m_kws.Cd.Credentials.UserName);
            msg.AddString(m_kws.Cd.Credentials.UserEmailAddress);
            msg.AddString(m_kws.Cd.Credentials.EmailID);

            // Send a ticket only if we're at the cached or ticket steps.
            byte[] ticket = null;
            if (m_currentStep != KwsLoginStep.Pwd) ticket = m_kws.Cd.Credentials.Ticket;
            msg.AddBin(ticket);

            // Send a password only if we're at the cached or password steps.
            String pwd = "";
            if (m_currentStep != KwsLoginStep.Ticket) pwd = m_kws.Cd.Credentials.Pwd;
            msg.AddString(pwd);

            // Post the login query.
            m_kws.PostKcdCmd(msg, HandleConnectKwsReply);
        }

        /// <summary>
        /// Called when the login reply is received.
        /// </summary>
        private void HandleConnectKwsReply(KcdQuery query)
        {
            KLogging.Log("Got login reply, kws " + m_kws.InternalID + ", status " + m_kws.Cd.MainStatus);

            Debug.Assert(m_ks.LoginStatus == KwsLoginStatus.LoggingIn);

            // This is the standard login reply.
            if (query.Res.Type == KAnp.KANP_RES_KWS_CONNECT_KWS)
            {
                // Get the provided information.
                KwsConnectRes r = new KwsConnectRes(query.Res);
                KLogging.Log(m_currentStep + " login step: " + r.ErrMsg);

                // Dispatch.
                if (r.Code == KAnp.KANP_KWS_LOGIN_OK) HandleConnectKwsSuccess(r);
                else if (r.Code == KAnp.KANP_KWS_LOGIN_BAD_PWD_OR_TICKET) HandleBadPwdOrTicket(r);
                else HandleLoginFailure(TranslateKcdLoginStatusCode(r.Code), new Exception(r.ErrMsg));
            }

            // This is an unexpected reply.
            else
            {
                HandleLoginFailure(KwsLoginResult.MiscKcdError, EAnpException.FromKAnpReply(query.Res));
            }
        }

        /// <summary>
        /// Called on successful login.
        /// </summary>
        private void HandleConnectKwsSuccess(KwsConnectRes r)
        {
            // Update our credentials and login information if needed.
            if (m_ks.LoginResult != KwsLoginResult.Accepted ||
                m_kws.Cd.Credentials.UserID != r.UserID ||
                m_kws.Cd.Credentials.EmailID != r.EmailID ||
                m_kws.Cd.Credentials.SecureFlag != r.SecureFlag ||
                m_kws.Cd.Credentials.KwmoAddress != r.KwmoAddress)
            {
                m_ks.LoginResult = KwsLoginResult.Accepted;
                m_ks.LoginResultString = "login successful";
                m_kws.Cd.Credentials.UserID = r.UserID;
                m_kws.Cd.Credentials.EmailID = r.EmailID;
                m_kws.Cd.Credentials.SecureFlag = r.SecureFlag;
                m_kws.Cd.Credentials.KwmoAddress = r.KwmoAddress;
                m_kws.OnStateChange(WmStateChange.Permanent);
            }

            // Remember the latest event ID available on the KCD.
            m_ks.LoginLatestEventId = r.LoginLatestEventID;

            // Tell the state machine.
            m_kws.Sm.HandleLoginSuccess();
        }

        /// <summary>
        /// Called when the login fails with KANP_KWS_LOGIN_BAD_PWD_OR_TICKET.
        /// </summary>
        private void HandleBadPwdOrTicket(KwsConnectRes r)
        {
            // Remember that the workspace is secure and if a password is available.
            m_kws.Cd.Credentials.SecureFlag = r.SecureFlag;
            m_ks.PwdPresentFlag = r.PwdOnKcdFlag;
            m_kws.OnStateChange(WmStateChange.Permanent);

            // The cached step has failed.
            if (m_currentStep == KwsLoginStep.Cached)
            {
                // Only the cached step was allowed. We're done.
                if (m_loginType == KwsLoginType.Cached)
                {
                    HandleLoginFailure(KwsLoginResult.BadSecurityCreds, new Exception("security credentials refused"));
                    return;
                }

                // We can perform the ticket step.
                if (KwmCfg.Cur.CanLoginOnKps())
                {
                    HandleTicketLoginStep();
                    return;
                }
            }

            // The ticket step has failed.
            else if (m_currentStep == KwsLoginStep.Ticket)
            {
                // Log the ticket refusal string.
                KLogging.Log("Ticket refused: " + r.ErrMsg);
            }

            // There is no password on the KCD.
            if (!m_ks.PwdPresentFlag)
            {
                HandleLoginFailure(KwsLoginResult.BadSecurityCreds,
                                   new Exception("a password must be assigned to you"));
                return;
            }

            // The password provided is bad.
            if (m_currentStep == KwsLoginStep.Pwd)
            {
                // Execute that asynchronously since we want our state to be
                // predictable.
                if (OnSetLoginPwdRefused != null) KBase.ExecInUI(OnSetLoginPwdRefused);
            }

            // We need a password.
            HandlePwdLoginStep();
        }

        /// <summary>
        /// Called when the disconnect reply is received.
        /// </summary>
        private void HandleDisconnectKwsReply(KcdQuery ctx)
        {
            // We're now logged out. Ignore failures from the server,
            // this can happen legitimately if we send a logout command
            // before we get the result of the login command.
            m_kws.Sm.HandleNormalLogout();
        }
    }
}
