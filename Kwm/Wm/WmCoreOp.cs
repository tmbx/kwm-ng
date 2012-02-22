using kcslib;
using kwmlib;
using System;
using System.Diagnostics;
using System.Net;
using System.Web;
using System.Collections.Generic;

namespace kwm
{
    /// <summary>
    /// Core operation (e.g. invite users) being processed in a workspace or
    /// in the workspace manager.
    /// </summary>
    public class WmCoreOp
    {
        /// <summary>
        /// True if the core operation has completed one way or another.
        /// The operation succeeded if ErrorEx is null.
        /// </summary>
        public bool DoneFlag = false;

        /// <summary>
        /// Exception representing the error that occurred, if any.
        /// </summary>
        public Exception ErrorEx = null;

        /// <summary>
        /// This delegated is called when the core operation has completed.
        /// </summary>
        public KBase.EmptyDelegate OnCompletion;

        /// <summary>
        /// Start the operation.
        /// </summary>
        public virtual void Start()
        {
        }

        /// <summary>
        /// Cancel the operation. The behavior behavior is to fail.
        /// </summary>
        public virtual void Cancel()
        {
            HandleFailure(new EAnpExCancelled());
        }

        /// <summary>
        /// Called when a unrecoverable error has been detected.
        /// </summary>
        public virtual void HandleFailure(Exception ex)
        {
            if (DoneFlag) return;
            ErrorEx = ex;
            Complete();
        }

        /// <summary>
        /// Parse the EAnp command. This method is defined here for convenience
        /// since it is easier to subclass one class than two. Nevertheless, the 
        /// core operation should remain independent of the EAnp protocol.
        /// </summary>
        public virtual void Parse(AnpMsg cmd)
        {
        }

        /// <summary>
        /// Format the EAnp reply to this query. 
        /// </summary>
        public void FormatReply(AnpMsg m)
        {
            if (ErrorEx != null) FormatFailureReply(m);
            else FormatSuccessReply(m);
        }

        /// <summary>
        /// Format the reply if the operation failed.
        /// </summary>
        protected virtual void FormatFailureReply(AnpMsg m)
        {
            m.Type = (UInt32)EAnpRes.Failure;
            EAnpException castedEx = EAnpException.FromException(ErrorEx);
            castedEx.Serialize(m);
        }

        /// <summary>
        /// Format the reply if the operation succeeded.
        /// </summary>
        protected virtual void FormatSuccessReply(AnpMsg m)
        {
            m.Type = (uint)EAnpRes.OK;
        }

        /// <summary>
        /// Clean up the state on completion.
        /// </summary>
        protected virtual void CleanUp()
        {
        }

        /// <summary>
        /// Cancel the HTTP query specified and set its reference to null,
        /// if needed.
        /// </summary>
        protected void ClearHttpQuery(ref HttpQuery query)
        {
            if (query != null)
            {
                query.Cancel();
                query = null;
            }
        }

        /// <summary>
        /// Cancel the KMOD query specified and set its reference to null, 
        /// if needed.
        /// </summary>
        protected void ClearKmodQuery(ref KmodQuery query)
        {
            if (query != null)
            {
                query.Cancel();
                query = null;
            }
        }

        /// <summary>
        /// Cancel the KCD query specified and set its reference to null, 
        /// if needed.
        /// </summary>
        protected void ClearKcdQuery(ref KcdQuery query)
        {
            if (query != null)
            {
                query.Terminate();
                query = null;
            }
        }

        /// <summary>
        /// This method should be called when the operation has completed.
        /// </summary>
        protected void Complete()
        {
            if (DoneFlag) return;
            DoneFlag = true;

            try
            {
                CleanUp();
                if (OnCompletion != null) OnCompletion();
            }
            
            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }
    }

    /// <summary>
    /// Core operation to register the user on a KPS. Let it be noted that
    /// having two poorly integrated login services lead to convoluted code.
    /// </summary>
    public class WmCoreOpRegisterKps : WmCoreOp
    {
        // Input.
        public bool FreemiumFlag;
        public String KpsAddr;
        public String KpsUserName;
        public String KpsUserPwd;

        // Output.
        public EAnpRegisterKpsCode ResCode = EAnpRegisterKpsCode.OK;
        public String ResMsg = "";

        /// <summary>
        /// Query to the freemium web service.
        /// </summary>
        private HttpQuery m_httpQuery = null;

        /// <summary>
        /// Query to KMOD/tbxsosd.
        /// </summary>
        private KmodQuery m_kmodQuery = null;

        /// <summary>
        /// Result code set by the HTTP query.
        /// </summary>
        private EAnpRegisterKpsCode m_httpResCode = EAnpRegisterKpsCode.OK;

        /// <summary>
        /// Exception raised by the HTTP query, on generic failure.
        /// </summary>
        private Exception m_httpEx = null;

        /// <summary>
        /// Result code set by the KMOD query.
        /// </summary>
        private EAnpRegisterKpsCode m_kmodResCode = EAnpRegisterKpsCode.OK;

        /// <summary>
        /// Exception raised by the KMOD query, on generic failure.
        /// </summary>
        private Exception m_kmodEx = null;

        /// <summary>
        /// Login token returned from KMOD.
        /// </summary>
        private String KpsLoginToken = "";

        /// <summary>
        /// Address of the KCD.
        /// </summary>
        private String KcdAddr = "";

        public override void Parse(AnpMsg cmd)
        {
            int i = 0;
            FreemiumFlag = cmd.Elements[i++].UInt32 > 0;
            KpsAddr = cmd.Elements[i++].String;
            KpsUserName = cmd.Elements[i++].String;
            KpsUserPwd = cmd.Elements[i++].String;
        }

        protected override void FormatSuccessReply(AnpMsg m)
        {
            m.Type = (uint)EAnpRes.RegisterKps;
            m.AddUInt32((uint)ResCode);
            m.AddString(ResMsg);
        }

        public override void Start()
        {
            // Update the registry. We set the host and login name and clear
            // the other login-related fields.
            KwmCfg cfg = KwmCfg.Spawn();
            UpdateRegistryObject(cfg);
            cfg.Commit();

            // Start the queries.
            if (FreemiumFlag) SubmitHttpQuery();
            SubmitKmodLoginQuery();
        }

        protected override void CleanUp()
        {
            ClearHttpQuery(ref m_httpQuery);
            ClearKmodQuery(ref m_kmodQuery);
        }

        /// <summary>
        /// Update the registry object specified with the current information.
        /// </summary>
        private void UpdateRegistryObject(KwmCfg cfg)
        {
            cfg.KpsAddr = KpsAddr;
            cfg.KpsUserName = KpsUserName;
            cfg.KpsLoginToken = KpsLoginToken;
            cfg.KpsKcdAddr = KcdAddr;
        }

        /// <summary>
        /// Submit the login query to KMOD.
        /// </summary>
        private void SubmitKmodLoginQuery()
        {
            K3p.K3pLoginTest cmd = new K3p.K3pLoginTest();
            cmd.Info.kps_login = KpsUserName;
            cmd.Info.kps_secret = KpsUserPwd;
            cmd.Info.secret_is_pwd = 1;
            cmd.Info.kps_net_addr = KpsAddr;
            cmd.Info.kps_port_num = 443;

            m_kmodQuery = new KmodQuery();
            m_kmodQuery.Submit(Wm.KmodBroker, new K3pCmd[] { cmd }, OnKmodLoginResult);
        }

        /// <summary>
        /// Called when the KMOD login query results are available.
        /// </summary>
        private void OnKmodLoginResult(KmodQuery query)
        {
            if (m_kmodQuery != query) return;
            m_kmodQuery = null;

            K3p.kmo_server_info_ack ack = query.OutMsg as K3p.kmo_server_info_ack;

            // Success.
            if (ack != null)
            {
                // Cache the login token.
                KpsLoginToken = ack.Token;

                // Submit the ticket query.
                SubmitKmodTicketQuery();
            }

            // Failure.
            else
            {
                m_kmodResCode = EAnpRegisterKpsCode.Failure;
                K3p.kmo_server_info_nack nack = query.OutMsg as K3p.kmo_server_info_nack;
                if (nack != null) m_kmodEx = new Exception(nack.Error);
                else m_kmodEx = new Exception(query.OutDesc);
                OnQueryCompletion();
            }
        }

        /// <summary>
        /// Submit the ticket query to KMOD.
        /// </summary>
        private void SubmitKmodTicketQuery()
        {
            WmLoginTicketQuery ticketQuery = new WmLoginTicketQuery();
            m_kmodQuery = ticketQuery;
            KwmCfg cfg = new KwmCfg();
            UpdateRegistryObject(cfg);
            ticketQuery.Submit(Wm.KmodBroker, cfg, OnKmodTicketResult);
        }

        /// <summary>
        /// Called when the KMOD ticket query results are available.
        /// </summary>
        private void OnKmodTicketResult(WmLoginTicketQuery query)
        {
            if (m_kmodQuery != query) return;
            m_kmodQuery = null;

            // Generic failure.
            if (query.Res != WmLoginTicketQueryRes.OK)
            {
                m_kmodResCode = EAnpRegisterKpsCode.Failure;
                m_kmodEx = new Exception(query.OutDesc);
            }

            // No KCD failure.
            else if (query.Ticket.KcdAddr == "")
                m_kmodResCode = EAnpRegisterKpsCode.NoKcd;

            // Query completed.
            OnQueryCompletion();
        }

        /// <summary>
        /// Submit the Freemium web query.
        /// </summary>
        private void SubmitHttpQuery()
        {
            String ws_url = "https://" + KpsAddr + "/freemium/registration";
            String email = HttpUtility.UrlEncode(KpsUserName);
            String pwd = HttpUtility.UrlEncode(KpsUserPwd);
            String post_params = "email=" + email + "&pwd=" + pwd;

            m_httpQuery = new HttpQuery(new Uri(ws_url + "?" + post_params));
            m_httpQuery.UseCache = false;
            m_httpQuery.OnHttpQueryEvent += HandleHttpQueryResult;
            m_httpQuery.StartQuery();
        }

        /// <summary>
        /// Called when the HTTP query results are available.
        /// </summary>
        private void HandleHttpQueryResult(Object sender, HttpQueryEventArgs args)
        {
            if (m_httpQuery == null) return;
            HttpQuery query = m_httpQuery;
            m_httpQuery = null;

            try
            {
                if (args.Type == HttpQueryEventType.Done)
                {
                    if (query.Result == "confirm") m_httpResCode = EAnpRegisterKpsCode.EmailConfirm;

                    // This shouldn't fail through here, but it did.
                    else if (query.Result != "ok") throw new Exception(query.Result);
                }

                else if (args.Type == HttpQueryEventType.DnsError) throw new Exception(args.Ex.Message);

                else if (args.Type == HttpQueryEventType.HttpError)
                {
                    // When the web services responds with a 403, the response
                    // body is not an error string to display to the user. It
                    // is a protocol response the can be safely used here for 
                    // exact match, to indicate the reason of failure of the 
                    // registration process.
                    if (query.StatusCode != HttpStatusCode.Forbidden) throw new Exception(args.Ex.Message);

                    String r = query.Result;
                    if (r == "registration_disabled") m_httpResCode = EAnpRegisterKpsCode.NoAutoRegister;
                    else if (r == "user_login_taken") m_httpResCode = EAnpRegisterKpsCode.LoginTaken;
                    else if (r == "user_registration_locked") m_httpResCode = EAnpRegisterKpsCode.NoLicense;
                    else throw new Exception("protocol error: " + r);
                }
            }

            catch (Exception ex)
            {
                m_httpResCode = EAnpRegisterKpsCode.Failure;
                m_httpEx = ex;
            }

            OnQueryCompletion();
        }

        /// <summary>
        /// This method must be called when the HTTP or KMOD query has 
        /// completed.
        /// </summary>
        private void OnQueryCompletion()
        {
            // Bail out if there is still a pending query.
            if (m_httpQuery != null || m_kmodQuery != null) return;

            // The tbxsosd registration succeeded. Ignore the result of the
            // HTTP query, it is irrelevant.
            if (m_kmodResCode == EAnpRegisterKpsCode.OK)
            {
                // Update the registry.
                KwmCfg cfg = KwmCfg.Spawn();
                UpdateRegistryObject(cfg);
                cfg.Commit();
            }

            // Use the error code set by the HTTP query.
            else if (m_httpResCode != EAnpRegisterKpsCode.OK)
            {
                ResCode = m_httpResCode;
                ResMsg = ErrorCodeToMsg(ResCode, m_httpEx);
            }

            // Use the error provided by KMOD/tbxsosd.
            else
            {
                ResCode = m_kmodResCode;
                ResMsg = ErrorCodeToMsg(ResCode, m_kmodEx);
            }

            // We're done.
            Complete();
        }

        /// <summary>
        /// Get the message corresponding to the error code specified.
        /// </summary>
        private String ErrorCodeToMsg(EAnpRegisterKpsCode code, Exception ex)
        {
            switch (code)
            {
                case EAnpRegisterKpsCode.EmailConfirm:
                    return "Please click on the link present in your confirmation email.";

                case EAnpRegisterKpsCode.NoAutoRegister:
                    return "Free registration is currently closed. Please try again later.";

                case EAnpRegisterKpsCode.LoginTaken:
                    return "The email address you entered is not available. Please pick another one.";

                case EAnpRegisterKpsCode.NoLicense:
                    return "The email address you entered is not available. Please pick another one.";

                case EAnpRegisterKpsCode.NoKcd:
                    return "No KCD server is available";

                case EAnpRegisterKpsCode.Failure:
                    return ex.Message;

                default:
                    return "";
            }
        }
    }

    /// <summary>
    /// Lookup the recipient addresses.
    /// </summary>
    public class WmCoreOpLookupRecAddr : WmCoreOp
    {
        /// <summary>
        /// Helper class representing a recipient.
        /// </summary>
        public class Rec
        {
            public String Email;
            public UInt64 KeyID;
            public String OrgName;
        }

        // Input.
        public List<String> EmailList = new List<String>();

        // Output.
        public List<Rec> RecList = new List<Rec>();

        /// <summary>
        /// Reference to the KMOD query.
        /// </summary>
        private KmodQuery m_kmodQuery;

        public override void Parse(AnpMsg cmd)
        {
            int i = 0;
            UInt32 nbRec = cmd.Elements[i++].UInt32;
            for (UInt32 j = 0; j < nbRec; j++) EmailList.Add(cmd.Elements[i++].String);
        }

        protected override void FormatSuccessReply(AnpMsg m)
        {
            m.Type = (uint)EAnpRes.LookupRecAddr;
            m.AddUInt32((uint)RecList.Count);
            foreach (Rec r in RecList)
            {
                m.AddString(r.Email);
                m.AddUInt64(r.KeyID);
                m.AddString(r.OrgName);
            }
        }

        public override void Start()
        {
            K3p.K3pSetServerInfo ssi = new K3p.K3pSetServerInfo();
            WmK3pServerInfo.RegToServerInfo(KwmCfg.Cur, ssi.Info);
            K3p.kpp_lookup_rec_addr lra = new K3p.kpp_lookup_rec_addr();
            lra.AddrArray = EmailList.ToArray();
            m_kmodQuery = new KmodQuery();
            m_kmodQuery.Submit(Wm.KmodBroker, new K3pCmd[] { ssi, lra }, HandleLookupRecAddrResult);
        }

        protected override void CleanUp()
        {
            ClearKmodQuery(ref m_kmodQuery);
        }

        /// <summary>
        /// Handle the results of the lookup query.
        /// </summary>
        private void HandleLookupRecAddrResult(KmodQuery query)
        {
            if (m_kmodQuery != query) return;
            m_kmodQuery = null;

            try
            {
                K3p.kmo_lookup_rec_addr res = query.OutMsg as K3p.kmo_lookup_rec_addr;

                // We got valid results.
                if (res != null)
                {
                    for (int i = 0; i < EmailList.Count; i++)
                    {
                        K3p.kmo_lookup_rec_addr_rec a = res.RecArray[i];
                        Rec r = new Rec();
                        RecList.Add(r);
                        r.Email = EmailList[i];
                        r.KeyID = (a.KeyID == "") ? 0 : Convert.ToUInt64(a.KeyID);
                        r.OrgName = a.OrgName;
                    }
                }

                else throw new Exception(query.OutDesc);
            }

            catch (Exception ex)
            {
                HandleFailure(ex);
                return;
            }

            Complete();
        }
    }
}