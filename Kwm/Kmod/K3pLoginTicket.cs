using kcslib;
using kwmlib;
using System;

namespace kwm
{
    /// <summary>
    /// Miscellaneous K3P methods.
    /// </summary>
    public static class WmK3pServerInfo
    {
        /// <summary>
        /// Fill the values of the server info specified based on the values of
        /// the registry specified.
        /// </summary>
        public static void RegToServerInfo(KwmCfg reg, K3p.kpp_server_info info)
        {
            if (reg.CanLoginOnKps())
            {
                info.kps_login = reg.KpsUserName;
                info.kps_secret = reg.KpsLoginToken;
                info.kps_net_addr = reg.KpsAddr;
                info.kps_port_num = 443;
            }

            else
            {
                info.kps_login = "";
                info.kps_secret = "";
                info.kps_net_addr = "";
                info.kps_port_num = 0;
            }
        }
    }

    /// <summary>
    /// Represent a workspace login ticket.
    /// </summary>
    public class WmLoginTicket
    {
        /// <summary>
        /// Base64 ticket.
        /// </summary>
        public String B64Ticket = null;

        /// <summary>
        /// Binary ticket.
        /// </summary>
        public byte[] BinaryTicket = null;

        /// <summary>
        /// ANP payload contained in the ticket.
        /// </summary>
        public AnpMsg AnpTicket = null;

        public String UserName = "";
        public String EmailAddr = "";
        public String KcdAddr = "";
        public UInt16 KcdPort = 0;
        public UInt64 KpsKeyID = 0;

        /// <summary>
        /// Parse the base64 ticket specified.
        /// </summary>
        public void FromB64Ticket(String b64Ticket)
        {
            B64Ticket = b64Ticket;
            FromBinaryTicket(Convert.FromBase64String(B64Ticket));
        }

        /// <summary>
        /// Parse the binary ticket specified. The base 64 ticket field is not
        /// modified.
        /// </summary>
        public void FromBinaryTicket(byte[] binaryTicket)
        {
            BinaryTicket = binaryTicket;
            if (BinaryTicket.Length < 38) throw new Exception("invalid ticket length");
            int payloadLength = BinaryTicket[37];
            payloadLength |= (BinaryTicket[36] << 8);
            payloadLength |= (BinaryTicket[35] << 16);
            payloadLength |= (BinaryTicket[34] << 24);
            byte[] strippedTicket = new byte[payloadLength];
            for (int i = 0; i < payloadLength; i++) strippedTicket[i] = BinaryTicket[i + 38];

            AnpTicket = new AnpMsg();
            AnpTicket.Elements = AnpMsg.ParsePayload(strippedTicket);
            UserName = AnpTicket.Elements[0].String;
            EmailAddr = AnpTicket.Elements[1].String;
            KcdAddr = AnpTicket.Elements[2].String;
            KcdPort = (UInt16)AnpTicket.Elements[3].UInt32;
            KpsKeyID = AnpTicket.Elements[4].UInt64;
        }
    }

    /// <summary>
    /// Result of a WmLoginTicketQuery.
    /// </summary>
    public enum WmLoginTicketQueryRes
    {
        /// <summary>
        /// Ticket obtained.
        /// </summary>
        OK,

        /// <summary>
        /// The configuration is invalid.
        /// </summary>
        InvalidCfg,

        /// <summary>
        /// A miscellaneous error occurred.
        /// </summary>
        MiscError
    }

    /// <summary>
    /// Method called when a login ticket query has completed.
    /// </summary>
    public delegate void WmLoginTicketQueryDelegate(WmLoginTicketQuery query);

    /// <summary>
    /// Query used to obtain a ticket using the credentials stored in the
    /// registry.
    /// </summary>
    public class WmLoginTicketQuery : KmodQuery
    {
        /// <summary>
        /// Method called when the login ticket query has completed.
        /// </summary>
        public WmLoginTicketQueryDelegate Callback2;

        /// <summary>
        /// Result of the query.
        /// </summary>
        public WmLoginTicketQueryRes Res;

        /// <summary>
        /// Login ticket obtained.
        /// </summary>
        public WmLoginTicket Ticket;

        /// <summary>
        /// Submit the query using the credentials obtained from the registry
        /// specified.
        /// </summary>
        public void Submit(WmKmodBroker broker, KwmCfg cfg, WmLoginTicketQueryDelegate callback)
        {
            // Set the proper callback.
            Callback2 = callback;

            // Fill out the server information.
            K3p.K3pSetServerInfo ssi = new K3p.K3pSetServerInfo();
            WmK3pServerInfo.RegToServerInfo(cfg, ssi.Info);

            // Submit the query.
            base.Submit(broker, new K3pCmd[] { ssi, new K3p.kpp_get_kws_ticket() }, AnalyseResults);
        }

        /// <summary>
        /// Update the values of the registry based on the results of the
        /// query, if required.
        /// </summary>
        public void UpdateRegistry()
        {
            if (Res == WmLoginTicketQueryRes.MiscError) return;

            KwmCfg reg = KwmCfg.Spawn();

            if (Res == WmLoginTicketQueryRes.InvalidCfg)
            {
                reg.KpsLoginToken = "";
                reg.KpsUserPower = 0;
                reg.KpsKcdAddr = "";
            }

            else if (Res == WmLoginTicketQueryRes.OK)
            {
                reg.KpsKcdAddr = Ticket.KcdAddr;
                reg.KpsUserPower = 0;
            }

            reg.Commit();
        }

        /// <summary>
        /// Analyze the results of the login query and call the callback.
        /// </summary>
        private void AnalyseResults(KmodQuery ignored)
        {
            if (OutMsg is K3p.kmo_invalid_config)
            {
                Res = WmLoginTicketQueryRes.InvalidCfg;
            }

            else if (OutMsg is K3p.kmo_get_kws_ticket)
            {
                try
                {
                    Res = WmLoginTicketQueryRes.OK;
                    Ticket = new WmLoginTicket();
                    Ticket.FromB64Ticket(((K3p.kmo_get_kws_ticket)OutMsg).Ticket);
                }

                catch (Exception ex)
                {
                    Res = WmLoginTicketQueryRes.MiscError;
                    OutDesc = "cannot parse ticket: " + ex.Message;
                }
            }

            else
            {
                Res = WmLoginTicketQueryRes.MiscError;
            }

            Callback2(this);
        }
    }
}