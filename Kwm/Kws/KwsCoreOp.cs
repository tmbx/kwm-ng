using kcslib;
using kwmlib;
using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace kwm
{
    /// <summary>
    /// Represent a core operation associated to a workspace.
    /// </summary>
    public abstract class KwsCoreOp : WmCoreOp
    {
        /// <summary>
        /// Workspace associated to the core operation, if any.
        /// </summary>
        public Workspace Kws = null;

        protected override void CleanUp()
        {
            UnregisterFromKws();
        }

        /// <summary>
        /// Start listening to the events fired by the workspace state machine.
        /// This method can be called more than once.
        /// </summary>
        public virtual void RegisterToKws()
        {
            Debug.Assert(Kws != null);
            Kws.OnKwsSmNotif += HandleKwsNotification;
        }

        /// <summary>
        /// Stop listening to the events fired by the workspace state machine.
        /// The workspace reference is not cleared. This method can be called
        /// more than once.
        /// </summary>
        public virtual void UnregisterFromKws()
        {
            if (Kws == null) return;
            Kws.OnKwsSmNotif -= HandleKwsNotification;
        }

        /// <summary>
        /// Handle a notification received from the workspace state machine. By
        /// default this method calls the default handlers.
        /// </summary>
        public virtual void HandleKwsNotification(Object sender, KwsSmNotif evt)
        {
            if (DoneFlag) return;

            else if (evt is KwsSmNotifKcdConn)
            {
                KwsSmNotifKcdConn e = evt as KwsSmNotifKcdConn;
                HandleKcdConn(e.Status, e.Ex);
            }

            else if (evt is KwsSmNotifKcdLogin)
            {
                KwsSmNotifKcdLogin e = evt as KwsSmNotifKcdLogin;
                HandleKwsLogin(e.Status, e.Ex);
            }

            else if (evt is KwsSmNotifTaskSwitch)
            {
                KwsSmNotifTaskSwitch e = evt as KwsSmNotifTaskSwitch;
                HandleTaskSwitch(e.Task, e.Ex);
            }
        }

        /// <summary>
        /// Called when the KCD connection status has changed. The default
        /// behavior is to fail on disconnection.
        /// </summary>
        public virtual void HandleKcdConn(KcdConnStatus status, Exception ex)
        {
            if (status == KcdConnStatus.Disconnecting || status == KcdConnStatus.Disconnected)
            {
                if (ex == null) ex = new EAnpExInterrupted();
                HandleFailure(ex);
            }
        }

        /// <summary>
        /// Called when the workspace login status has changed. The default
        /// behavior is to fail on log out.
        /// </summary>
        public virtual void HandleKwsLogin(KwsLoginStatus status, Exception ex)
        {
            if (status == KwsLoginStatus.LoggingOut || status == KwsLoginStatus.LoggedOut)
            {
                if (ex == null) ex = new EAnpExInterrupted();
                HandleFailure(ex);
            }
        }

        /// <summary>
        /// Called when the current workspace task is switching. The default
        /// behavior is to fail if an exception occurred or if the new task
        /// is not Spawn or WorkOnline.
        /// </summary>
        public virtual void HandleTaskSwitch(KwsTask task, Exception ex)
        {
            if (ex != null ||
                (task != KwsTask.Spawn && task != KwsTask.WorkOnline))
            {
                if (ex == null) ex = new EAnpExInterrupted();
                HandleFailure(ex);
            }
        }
    }

    /// <summary>
    /// Core operation used to send a KCD command to a workspace and wait for
    /// its result.
    /// </summary>
    public abstract class KwsCoreOpKcdQuery : KwsCoreOp
    {
        /// <summary>
        /// Reference to the KCD query.
        /// </summary>
        private KcdQuery m_kcdQuery = null;

        /// <summary>
        /// This method is called in Start() to setup the environment.
        /// </summary>
        protected virtual void PrepareStart()
        {
        }

        /// <summary>
        /// Prepare the command to send to the KCD.
        /// </summary>
        protected virtual void PrepareCmd(AnpMsg cmd)
        {
        }

        /// <summary>
        /// Handle the result of the commands.
        /// </summary>
        protected virtual void HandleCmdResult(AnpMsg res)
        {
            if (res.Type != KAnp.KANP_RES_OK) throw EAnpException.FromKAnpReply(res);
        }

        public override void Start()
        {
            try
            {
                // Prepare to start.
                PrepareStart();

                // Register to the workspace.
                RegisterToKws();

                // Make sure the workspace is logged in.
                if (Kws.Cd.KcdState.LoginStatus != KwsLoginStatus.LoggedIn)
                    throw new EAnpExInterrupted();

                // Post the command.
                AnpMsg cmd = Kws.NewKcdCmd(0);
                PrepareCmd(cmd);
                m_kcdQuery = Kws.PostKcdCmd(cmd, OnKcdQueryResult);
            }

            catch (Exception ex)
            {
                HandleFailure(ex);
            }
        }

        protected override void CleanUp()
        {
            UnregisterFromKws();
            ClearKcdQuery(ref m_kcdQuery);
        }

        /// <summary>
        /// Called when the create workspace command reply is received.
        /// </summary>
        private void OnKcdQueryResult(KcdQuery query)
        {
            if (m_kcdQuery != query) return;
            m_kcdQuery = null;

            try
            {
                HandleCmdResult(query.Res);
                Complete();
            }

            catch (Exception ex)
            {
                HandleFailure(ex);
            }
        }
    }

    /// <summary>
    /// Core operation used to the delete a workspace remotely.
    /// </summary>
    public class KwsCoreOpDeleteKwsRemotely : KwsCoreOp
    {
        public override void Start()
        {
            RegisterToKws();
            Kws.Sm.RequestTaskSwitch(KwsTask.DeleteRemotely);
        }

        protected override void CleanUp()
        {
            UnregisterFromKws();

            // There was an error.
            if (ErrorEx != null)
            {
                // Stop the workspace if it was created successfully, otherwise
                // delete it.
                if (Kws.Cd.MainStatus == KwsMainStatus.NotYetSpawned)
                    Kws.Sm.RequestTaskSwitch(KwsTask.DeleteLocally);
                else
                    Kws.Sm.RequestTaskSwitch(KwsTask.Stop);
            }

            // Success. Delete the workspace locally.
            else
            {
                Kws.Sm.RequestTaskSwitch(KwsTask.DeleteLocally);
            }
        }

        public override void HandleKcdConn(KcdConnStatus status, Exception ex)
        {
            // Something went wrong.
            if (ex != null) HandleFailure(ex);

            // Check if we're now connected as we wanted to.
            else UpdateDeleteRemotelyStepIfNeeded();
        }

        public override void HandleKwsLogin(KwsLoginStatus status, Exception ex)
        {
            // The workspace has been deleted remotely.
            if (Kws.Cd.KcdState.LoginResult == KwsLoginResult.DeletedKws) Complete();

            // Something went wrong.
            else if (ex != null) HandleFailure(ex);
            else if (status == KwsLoginStatus.LoggedIn) HandleFailure(new EAnpExInterrupted());

            // Check if we're now logged out as we wanted to.
            else UpdateDeleteRemotelyStepIfNeeded();
        }

        public override void HandleTaskSwitch(KwsTask task, Exception ex)
        {
            // Something went wrong.
            if (ex != null) HandleFailure(ex);
            else if (task != KwsTask.DeleteRemotely) HandleFailure(new EAnpExInterrupted());

            // Check if we're ready to pass to the next step.
            else UpdateDeleteRemotelyStepIfNeeded();
        }

        /// <summary>
        /// This method should be called when the delete remotely step may
        /// need to be updated. This can be immediately after the task switch, 
        /// when the KCD becomes connected or the workspace becomes logged out.
        /// Basically, what we're trying to do is to log out, get the KCD 
        /// connected and then login with a request to delete the workspace.
        /// </summary>
        private void UpdateDeleteRemotelyStepIfNeeded()
        {
            if (Kws.Kcd.ConnStatus == KcdConnStatus.Connected &&
                Kws.Cd.KcdState.LoginStatus == KwsLoginStatus.LoggedOut)
            {
                Kws.Sm.SetDeleteRemotelyStep(KwsDeleteRemotelyStep.Login);
            }
        }
    }

    /// <summary>
    /// Core operation used to change the task of a workspace.
    /// </summary>
    public class KwsCoreOpSetKwsTask : KwsCoreOp
    {
        // Input.
        public UInt64 KwsID;
        public KwsTask Task;

        /// <summary>
        /// Core operation used to delete the workspace remotely.
        /// </summary>
        private KwsCoreOpDeleteKwsRemotely m_deleteRemotelyOp = null;

        public override void Parse(AnpMsg cmd)
        {
            int i = 0;
            KwsID = cmd.Elements[i++].UInt64;
            Task = (KwsTask)cmd.Elements[i++].UInt32;
        }

        public override void Start()
        {
            try
            {
                // Get the workspace, if possible.
                Kws = Wm.GetKwsByInternalIDOrThrow(KwsID);

                // If the task is delete remotely, start a delete remotely
                // operation.
                if (Task == KwsTask.DeleteRemotely)
                {
                    m_deleteRemotelyOp = new KwsCoreOpDeleteKwsRemotely();
                    m_deleteRemotelyOp.Kws = Kws;
                    m_deleteRemotelyOp.OnCompletion += OnDeleteRemotelyCompletion;
                    m_deleteRemotelyOp.Start();
                }

                // Perform the task switch right away.
                else
                {
                    Kws.Sm.RequestTaskSwitch(Task);
                }
            }

            catch (Exception ex)
            {
                HandleFailure(ex);
            }
        }

        protected override void CleanUp()
        {
            if (m_deleteRemotelyOp != null)
            {
                m_deleteRemotelyOp.OnCompletion -= OnDeleteRemotelyCompletion;
                m_deleteRemotelyOp.Cancel();
                m_deleteRemotelyOp = null;
            };
        }

        /// <summary>
        /// Called when the delete remotely operation has completed.
        /// </summary>
        private void OnDeleteRemotelyCompletion()
        {
            if (DoneFlag) return;
            if (m_deleteRemotelyOp.ErrorEx != null) HandleFailure(m_deleteRemotelyOp.ErrorEx);
            else Complete();
        }
    }

    /// <summary>
    /// Core operation used to wait for the result of the SetLoginPwd command.
    /// </summary>
    public class KwsCoreOpSetLoginPwd : KwsCoreOp
    {
        // Input.
        public UInt64 KwsID;
        public String Pwd;

        public override void Parse(AnpMsg cmd)
        {
            int i = 0;
            KwsID = cmd.Elements[i++].UInt64;
            Pwd = cmd.Elements[i++].String;
        }

        public override void Start()
        {
            try
            {
                // Register to the workspace, if possible.
                Kws = Wm.GetKwsByInternalIDOrThrow(KwsID);
                RegisterToKws();
                Kws.KcdLoginHandler.OnSetLoginPwdRefused += OnLoginPwdRefused;

                // The password was "accepted" without being tried.
                if (!Kws.KcdLoginHandler.SetLoginPwd(Pwd))
                {
                    Complete();
                    return;
                }

                // Wait to be notified. Either we will be told that the 
                // password was refused or the login status will change.
            }

            catch (Exception ex)
            {
                HandleFailure(ex);
            }
        }

        protected override void CleanUp()
        {
            if (Kws != null) Kws.KcdLoginHandler.OnSetLoginPwdRefused -= OnLoginPwdRefused;
            UnregisterFromKws();
        }

        public override void HandleKwsLogin(KwsLoginStatus status, Exception ex)
        {
            // Success.
            if (status == KwsLoginStatus.LoggedIn)
                Complete();

            // Interrupted.
            else if (status == KwsLoginStatus.LoggingOut || status == KwsLoginStatus.LoggedOut)
                HandleFailure(new EAnpExInterrupted());
        }

        /// <summary>
        /// Called when the login password is refused.
        /// </summary>
        private void OnLoginPwdRefused()
        {
            HandleFailure(new EAnpExInvalidKwsLoginPwd());
        }
    }

    /// <summary>
    /// Core operation used to create a workspace on the KCD.
    /// </summary>
    public class KwsCoreOpCreateKws : KwsCoreOp
    {
        private enum OpStep
        {
            Initial,
            TicketReply,
            Connecting,
            CreateReply,
            LoggingIn
        }

        // Input.
        public KwsCredentials Creds = new KwsCredentials();

        /// <summary>
        /// Reference to the KMOD query.
        /// </summary>
        private KmodQuery m_kmodQuery = null;

        /// <summary>
        /// Reference to the KCD query.
        /// </summary>
        private KcdQuery m_kcdQuery = null;

        /// <summary>
        /// Current operation step.
        /// </summary>
        private OpStep m_step = OpStep.Initial;

        public override void Parse(AnpMsg cmd)
        {
            int i = 0;
            Creds.KwsName = cmd.Elements[i++].String;
            Creds.Flags = cmd.Elements[i++].UInt32;
        }

        protected override void FormatSuccessReply(AnpMsg m)
        {
            m.Type = (uint)EAnpRes.CreateKws;
            m.AddUInt64(Kws.InternalID);
        }

        public override void Start()
        {
            try
            {
                // Make sure we can login on the KPS.
                if (!KwmCfg.Cur.CanLoginOnKps()) throw new EAnpExInvalidKpsConfig();

                // Get a ticket.
                m_step = OpStep.TicketReply;
                WmLoginTicketQuery ticketQuery = new WmLoginTicketQuery();
                m_kmodQuery = ticketQuery;
                ticketQuery.Submit(Wm.KmodBroker, KwmCfg.Cur, OnKmodTicketResult);
            }

            catch (Exception ex)
            {
                HandleFailure(ex);
            }
        }

        protected override void CleanUp()
        {
            UnregisterFromKws();
            ClearKmodQuery(ref m_kmodQuery);
            ClearKcdQuery(ref m_kcdQuery);

            // Delete the workspace locally on error.
            if (ErrorEx != null && Kws != null) Kws.Sm.RequestTaskSwitch(KwsTask.DeleteLocally);
        }

        public override void HandleKcdConn(KcdConnStatus status, Exception ex)
        {
            // Something went wrong.
            if (ex != null) HandleFailure(ex);
            else if ((status == KcdConnStatus.Disconnected || status == KcdConnStatus.Disconnecting) &&
                     m_step >= OpStep.CreateReply) HandleFailure(new EAnpExInterrupted());

            // Send the creation command if needed. 
            else SendCreateKwsCmdIfNeeded();
        }

        public override void HandleKwsLogin(KwsLoginStatus status, Exception ex)
        {
            // Something went wrong.
            if (ex != null) HandleFailure(ex);

            // Complete the operation if needed.
            else CompleteIfNeeded();
        }

        /// <summary>
        /// Called when the KMOD ticket query results are available.
        /// </summary>
        private void OnKmodTicketResult(WmLoginTicketQuery query)
        {
            if (m_kmodQuery != query) return;
            m_kmodQuery = null;
            Debug.Assert(m_step == OpStep.TicketReply);

            try
            {
                // Update the registry information.
                query.UpdateRegistry();

                // Generic failure.
                if (query.Res != WmLoginTicketQueryRes.OK) throw new Exception(query.OutDesc);

                // We cannot create a workspace.
                if (!KwmCfg.Cur.CanCreateKws()) throw new EAnpExInvalidKpsConfig();

                // Update the credentials.
                Creds.Ticket = query.Ticket.BinaryTicket;
                Creds.UserName = query.Ticket.AnpTicket.Elements[0].String;
                Creds.UserEmailAddress = query.Ticket.AnpTicket.Elements[1].String;

                // Set the KCD address.
                if (KwmCfg.Cur.CustomKcdFlag) Creds.KcdAddress = KwmCfg.Cur.CustomKcdAddress;
                else Creds.KcdAddress = KwmCfg.Cur.KpsKcdAddr;
                if (Creds.KcdAddress == "") throw new Exception("invalid KCD address");

                // Create the workspace object.
                Kws = Wm.CreateWorkspace(Creds);
                RegisterToKws();

                // Start the spawn operation and wait for the connection.
                m_step = OpStep.Connecting;
                Kws.Sm.RequestTaskSwitch(KwsTask.Spawn);
                SendCreateKwsCmdIfNeeded();
            }

            catch (Exception ex)
            {
                HandleFailure(ex);
            }
        }

        /// <summary>
        /// Send the workspace creation command if we are ready to.
        /// </summary>
        private void SendCreateKwsCmdIfNeeded()
        {
            if (DoneFlag || m_step != OpStep.Connecting || Kws.Kcd.ConnStatus != KcdConnStatus.Connected) return;

            m_step = OpStep.CreateReply;
            AnpMsg cmd = Wm.NewKcdCmd(Kws.Kcd.MinorVersion, KAnp.KANP_CMD_MGT_CREATE_KWS);
            cmd.AddString(Creds.KwsName);
            cmd.AddBin(Creds.Ticket);
            cmd.AddUInt32(Convert.ToUInt32(Creds.PublicFlag));
            cmd.AddUInt32(Convert.ToUInt32(Creds.SecureFlag));
            if (cmd.Minor >= 4) cmd.AddUInt32(Convert.ToUInt32(Creds.ThinKfsFlag));
            m_kcdQuery = Kws.PostKcdCmd(cmd, HandleCreateKwsCmdResult);
        }

        /// <summary>
        /// Called when the create workspace command reply is received.
        /// </summary>
        private void HandleCreateKwsCmdResult(KcdQuery query)
        {
            if (m_kcdQuery != query) return;
            m_kcdQuery = null;
            Debug.Assert(m_step == OpStep.CreateReply);

            try
            {
                AnpMsg res = query.Res;

                // Failure.
                if (res.Type != KAnp.KANP_RES_MGT_KWS_CREATED) throw EAnpException.FromKAnpReply(res);

                // Parse the reply.
                UInt64 externalID = res.Elements[0].UInt64;
                String emailID = res.Elements[1].String;

                // Validate that the KCD is not screwing with us. This can 
                // happen if the KCD state has been reverted.
                if (Wm.GetKwsByExternalID(Kws.Kcd.KcdID, externalID) != null)
                    throw new Exception("duplicate " + KwmStrings.Kws + " external ID");

                // Update the workspace credentials.
                Creds.ExternalID = externalID;
                Creds.EmailID = emailID;

                // Wait for login.
                m_step = OpStep.LoggingIn;
                Kws.Sm.SetLoginType(KwsLoginType.Cached);
                Kws.Sm.SetSpawnStep(KwsSpawnTaskStep.Login);
            }

            catch (Exception ex)
            {
                HandleFailure(ex);
            }
        }

        /// <summary>
        /// Complete the operation if we are ready to.
        /// </summary>
        private void CompleteIfNeeded()
        {
            if (DoneFlag ||
                m_step != OpStep.LoggingIn ||
                Kws.Cd.KcdState.LoginStatus != KwsLoginStatus.LoggedIn ||
                Kws.Cd.MainStatus != KwsMainStatus.NotYetSpawned ||
                Kws.Cd.CurrentTask != KwsTask.Spawn) return;

            // Update the main status of the workspace.
            Kws.Cd.MainStatus = KwsMainStatus.Good;
            Kws.OnStateChange(WmStateChange.Permanent);

            // Ask the state machine to work online.
            Kws.Sm.RequestTaskSwitch(KwsTask.WorkOnline);

            // Serialize the KWM so we don't lose the workspace if the KWM
            // crashes.
            Wm.Serialize();

            // We're done.
            Complete();
        }
    }

    /// <summary>
    /// Core operation used to invite users in a workspace.
    /// </summary>
    public class KwsCoreOpInviteKws : KwsCoreOpKcdQuery
    {
        /// <summary>
        /// Represent a user to invite.
        /// </summary>
        public class User
        {
            /// <summary>
            /// Name of the user, if any.
            /// </summary>
            public String UserName = "";

            /// <summary>
            /// User email address.
            /// </summary>
            public String EmailAddress = "";

            /// <summary>
            /// Inviter-specified password, if any.
            /// </summary>
            public String Pwd = "";

            /// <summary>
            /// User key ID. If none, set to 0.
            /// </summary>
            public UInt64 KeyID = 0;

            /// <summary>
            /// User organization's name, if any.
            /// </summary>
            public String OrgName = "";

            /// <summary>
            /// Email ID used to invite the user, if any.
            /// </summary>
            public String EmailID;

            /// <summary>
            /// URL that should appear in the invitation mail.
            /// </summary>
            public String Url;

            /// <summary>
            /// Invitation error for the user, if any.
            /// </summary>
            public String Error;
        }

        // Input.
        public UInt64 KwsId;
        public List<User> UserList = new List<User>();
        public bool KcdSendEmailFlag = false;
        public String InvitationMsg = "";

        // Output.
        public String Wleu = "";

        public override void Parse(AnpMsg cmd)
        {
            int i = 0;
            KwsId = cmd.Elements[i++].UInt64;
            KcdSendEmailFlag = cmd.Elements[i++].UInt32 > 0;
            InvitationMsg = cmd.Elements[i++].String;
            UInt32 nbUser = cmd.Elements[i++].UInt32;
            for (UInt32 j = 0; j < nbUser; j++)
            {
                User u = new User();
                UserList.Add(u);
                u.UserName = cmd.Elements[i++].String;
                u.EmailAddress = cmd.Elements[i++].String;
                u.KeyID = cmd.Elements[i++].UInt64;
                u.OrgName = cmd.Elements[i++].String;
                u.Pwd = cmd.Elements[i++].String;
            }
        }

        protected override void FormatSuccessReply(AnpMsg m)
        {
            m.Type = (uint)EAnpRes.InviteKws;
            m.AddString(Wleu);
            m.AddUInt32((uint)UserList.Count);
            foreach (User u in UserList)
            {
                m.AddString(u.EmailAddress);
                m.AddString(u.Url);
                m.AddString(u.Error);
            }
        }

        protected override void PrepareStart()
        {
            Kws = Wm.GetKwsByInternalIDOrThrow(KwsId);
        }

        protected override void PrepareCmd(AnpMsg cmd)
        {
            cmd.Type = (uint)KAnp.KANP_CMD_KWS_INVITE_KWS;
            cmd.AddString(InvitationMsg);
            cmd.AddUInt32((UInt32)UserList.Count);
            foreach (User u in UserList)
            {
                cmd.AddString(u.UserName);
                cmd.AddString(u.EmailAddress);
                cmd.AddUInt64(u.KeyID);
                cmd.AddString(u.OrgName);
                cmd.AddString(u.Pwd);
                cmd.AddUInt32(Convert.ToUInt32(KcdSendEmailFlag));
            }
        }

        protected override void HandleCmdResult(AnpMsg res)
        {
            if (res.Type != KAnp.KANP_RES_KWS_INVITE_KWS) throw EAnpException.FromKAnpReply(res);

            int i = 0;
            Wleu = res.Elements[i++].String;
            i++;
            foreach (User u in UserList)
            {
                u.EmailID = res.Elements[i++].String;
                u.Url = res.Elements[i++].String;
                u.Error = res.Elements[i++].String;
            }
        }
    }

    /// <summary>
    /// Core operation used to post a chat message.
    /// </summary>
    public class KwsCoreOpChatPostMsg : KwsCoreOpKcdQuery
    {
        // Input.
        public UInt64 KwsId;
        public UInt32 ChannelID;
        public String Msg;

        public override void Parse(AnpMsg cmd)
        {
            int i = 0;
            KwsId = cmd.Elements[i++].UInt64;
            ChannelID = cmd.Elements[i++].UInt32;
            Msg = cmd.Elements[i++].String;
        }

        protected override void PrepareStart()
        {
            Kws = Wm.GetKwsByInternalIDOrThrow(KwsId);
        }

        protected override void PrepareCmd(AnpMsg cmd)
        {
            cmd.Type = (uint)KAnp.KANP_CMD_CHAT_MSG;
            cmd.AddUInt32(ChannelID);
            cmd.AddString(Msg);
        }
    }

    /// <summary>
    /// Core operation used to accept a chat request.
    /// </summary>
    public class KwsCoreOpPbAcceptChat : KwsCoreOpKcdQuery
    {
        // Input.
        public UInt64 KwsId;
        public UInt32 UserID;
        public UInt64 RequestID;

        public override void Parse(AnpMsg cmd)
        {
            int i = 0;
            KwsId = cmd.Elements[i++].UInt64;
            UserID = cmd.Elements[i++].UInt32;
            RequestID = cmd.Elements[i++].UInt64;
        }

        protected override void PrepareStart()
        {
            Kws = Wm.GetKwsByInternalIDOrThrow(KwsId);
        }

        protected override void PrepareCmd(AnpMsg cmd)
        {
            cmd.Type = (uint)KAnp.KANP_CMD_PB_ACCEPT_CHAT;
            cmd.AddUInt64(RequestID);
            cmd.AddUInt32(UserID);
            cmd.AddUInt32(UserID);
        }
    }
}