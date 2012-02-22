/*
 * Taken from k3p_core_defs.h in kmo.
 *
 * Copyright (C) 2006-2012 Opersys Inc., All rights reserved.
 *
 * Authors:
 *    Mathieu Lemay, original edit.
 *    Karim Yaghmour, "a few" renamings and reformatting ;)
 *
 * Core definitions for communication protocol between misc. plugins and kmo.
 *
 * Acronyms:
 *    KMO, Kryptiva Mail Operator
 *    KPP, Kryptiva Packaging Plugin
 *    K3P, KMO Plugin Pipe Protocol
 *    OTUT, One-Time Use Token (for secure reply to sender)
 *
 * Notes :
 *    Putting it politely, mail clients in general vary greatly in the quality
 *    of their designs, their code maturity, and general sanity. It's fair to
 *    say most mail clients seem to have been hacked in order to be
 *    compatible with as much of what goes around as possible. As a result,
 *    there is hardly any general framework that can be assumed to be true
 *    for *all* mail clients. So ... K3P is built to require as little
 *    intelligence as possible on the part of the mail client.
 *
 *    It is assumed the mail client is sane enough to maintain an internal
 *    *unique* per-message identifier _and_ that we have access to it.
 *    Without this, it becomes very difficult to rapidly report back to the
 *    interface info regarding already processed messages (i.e. mails that
 *    we previously received and had already put through KMO.) We can
 *    always use a hash as the ID, but that's far from optimal.
 */

using kcslib;
using kwmlib;
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace kwm
{
    public class K3pException : Exception
    {
        public K3pException(String message) : base(message) { }
        public K3pException(String message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Represent a command or a result received from KMOD.
    /// </summary>
    public abstract class K3pMsg
    {
        public static void WriteInst(BinaryWriter w, UInt32 value)
        {
            w.Write("INS".ToCharArray());
            w.Write(value.ToString("x8").ToCharArray());
        }
        public static void WriteInt(BinaryWriter w, UInt32 value)
        {
            w.Write("INT".ToCharArray());
            w.Write(value.ToString().ToCharArray());
            w.Write(">".ToCharArray());
        }

        public static void WriteString(BinaryWriter w, String value)
        {
            w.Write("STR".ToCharArray());
            w.Write(value.Length.ToString().ToCharArray());
            w.Write(">".ToCharArray());
            w.Write(value.ToCharArray());
        }

        /// <summary>
        /// Write a K3P message field to the stream specified.
        /// </summary>
        private void InternalToStream(BinaryWriter w, object value)
        {
            Type t = value.GetType();
            if (t.IsArray)
            {
                Array a = (Array)value;
                WriteInt(w, (UInt32)a.Length);
                foreach (object o in a)
                {
                    InternalToStream(w, o);
                }
            }
            else if (value is K3pMsg)
                ((K3pMsg)value).ToStream(w);
            else if (t == typeof(String))
                WriteString(w, (String)value);
            else if (t == typeof(UInt32))
                WriteInt(w, (UInt32)value);
            else if (t.IsEnum)
                WriteInt(w, (UInt32)((Enum)value).GetHashCode());
            else
                throw new K3pException("unsupported type " + t.FullName + " in K3P");
        }

        /// <summary>
        /// Write the K3P message to the stream specified. This is done using
        /// reflection.
        /// </summary>
        public void ToStream(BinaryWriter w)
        {
            K3pCmd cmd = this as K3pCmd;
            if (cmd != null)
            {
                WriteInst(w, cmd.InputIns);
            }

            // When this class derives from K3pCmd, the instruction from K3pCmd
            // is encountered last. We must skip it.
            FieldInfo[] info = this.GetType().GetFields();
            for (int i = 0; i < ((cmd == null) ? info.Length : info.Length - 1); i++)
            {
                FieldInfo fi = info[i];
                try
                {
                    object value = fi.GetValue(this);
                    InternalToStream(w, value);
                }
                catch (Exception e)
                {
                    throw new K3pException("Cannot convert " + this.GetType().FullName + "." + fi.Name + " in binary", e);
                }
            }
        }

        public void ToStream(Stream s)
        {
            BinaryWriter w = new BinaryWriter(s, Encoding.GetEncoding("iso-8859-1"));
            ToStream(w);
        }

        /// <summary>
        /// Return the value of the K3P message field specified.
        /// </summary>
        private Object InternalFromElementReader(K3pElementReader r, Type t)
        {
            if (t == typeof(UInt32)) return r.Int();
            else if (t.IsEnum) return Enum.ToObject(t, r.Int());
            else if (t == typeof(String)) return r.Str();
            else if (t == typeof(byte[])) return r.Bin();

            else if (t.IsArray)
            {
                Type elType = t.GetElementType();
                UInt32 size = r.Int();
                Array a = Array.CreateInstance(elType, size);
                for (UInt32 i = 0; i < size; i++)
                    a.SetValue(InternalFromElementReader(r, elType), i);
                return a;
            }

            else if (t.IsSubclassOf(typeof(K3pMsg)))
            {
                K3pMsg m = (K3pMsg)Activator.CreateInstance(t);
                m.FromElementReader(r);
                return m;
            }

            else throw new K3pException("unsupported type " + t.FullName + " in K3P");
        }

        /// <summary>
        /// Read the message content from the reader specified.
        /// </summary>
        public void FromElementReader(K3pElementReader r)
        {
            foreach (FieldInfo fi in this.GetType().GetFields())
            {
                fi.SetValue(this, InternalFromElementReader(r, fi.FieldType));
            }
        }
    }

    /// <summary>
    /// K3P message used as a command.
    /// </summary>
    public class K3pCmd : K3pMsg
    {
        /// <summary>
        /// Input instruction.
        /// </summary>
        public UInt32 InputIns;

        public K3pCmd(UInt32 inputIns)
        {
            InputIns = inputIns;
        }
    }

    /// <summary>
    /// Method returning the next K3P element received from KMOD.
    /// </summary>
    public delegate K3pElement K3pElementReaderMethod();

    /// <summary>
    /// Class reading K3P elements using the method specified.
    /// </summary>
    public class K3pElementReader
    {
        /// <summary>
        /// Read the next K3P element.
        /// </summary>
        public K3pElementReaderMethod Reader;

        public K3pElementReader(K3pElementReaderMethod reader)
        {
            Reader = reader;
        }

        public UInt32 Ins() { return GetNext(K3pElement.K3pType.INS).Ins; }
        public UInt32 Int() { return GetNext(K3pElement.K3pType.INT).Int; }
        public String Str() { return GetNext(K3pElement.K3pType.STR).Str; }
        public byte[] Bin() { return GetNext(K3pElement.K3pType.STR).Bin; }

        /// <summary>
        /// Return the next element if it has the expected type. Throw an
        /// exception otherwise.
        /// </summary>
        private K3pElement GetNext(K3pElement.K3pType expected)
        {
            K3pElement el = Reader();
            CheckType(expected, el.Type);
            return el;
        }

        /// <summary>
        /// Throw an exception if the expected type does not match the actual 
        /// type.
        /// </summary>
        private void CheckType(K3pElement.K3pType expected, K3pElement.K3pType actual)
        {
            if (expected != actual)
                throw new K3pException("expected K3P " + K3pElement.GetTypeName(expected) +
                                       ", received K3P " + K3pElement.GetTypeName(actual));
        }
    }

    /// <summary>
    /// Extension of the basic K3pElementReader used to slurp a result from
    /// KMOD.
    /// </summary>
    public class K3pSlurper : K3pElementReader
    {
        public K3pSlurper(K3pElementReaderMethod reader)
            : base(reader)
        {
        }

        /// <summary>
        /// Slurp a result according to the input command specified. The method
        /// throws an exception if it cannot slurp a valid result. The method 
        /// sets outDesc to a string describing the result obtained and outMsg
        /// to the result obtained.
        /// </summary>
        public void Slurp(K3pCmd inCmd, out String outDesc, out K3pMsg outMsg)
        {
            outDesc = "unexpected result";
            UInt32 outputIns = Ins();

            if (outputIns == K3p.KMO_INVALID_REQ)
                throw new Exception("invalid request");

            else if (outputIns == K3p.KMO_INVALID_CONFIG)
            {
                outMsg = new K3p.kmo_invalid_config();
                outDesc = "invalid configuration";
            }

            else if (outputIns == K3p.KMO_SERVER_ERROR)
            {
                K3p.kmo_server_error m = new K3p.kmo_server_error();
                SlurpHelper(m, out outMsg);
                outDesc = "cannot contact ";
                String name = "server";
                if (m.sid == K3p.kmo_server_error.Kmo_Sid.KMO_SID_KPS) name = "KPS";
                else if (m.sid == K3p.kmo_server_error.Kmo_Sid.KMO_SID_OPS) name = "OPS";
                else if (m.sid == K3p.kmo_server_error.Kmo_Sid.KMO_SID_OUS) name = "OUS";
                else if (m.sid == K3p.kmo_server_error.Kmo_Sid.KMO_SID_OTS) name = "OTS";
                else if (m.sid == K3p.kmo_server_error.Kmo_Sid.KMO_SID_IKS) name = "IKS";
                else if (m.sid == K3p.kmo_server_error.Kmo_Sid.KMO_SID_EKS) name = "EKS";
                outDesc += name + ": ";
                String reason = m.message;
                if (m.error == K3p.kmo_server_error.Kmo_Serror.KMO_SERROR_TIMEOUT)
                    reason = "timeout occurred";
                else if (m.error == K3p.kmo_server_error.Kmo_Serror.KMO_SERROR_UNREACHABLE)
                    reason = "host unreachable";
                outDesc += reason;
            }

            else if (outputIns == K3p.KMO_MUST_UPGRADE)
            {
                K3p.kmo_must_upgrade m = new K3p.kmo_must_upgrade();
                SlurpHelper(m, out outMsg);
                outDesc = "must upgrade ";
                String what = "plugin";
                if (m.what == K3p.KMO_UPGRADE_KPS) what = "KPS";
                outDesc += what;
            }

            else if (outputIns == K3p.KMO_SERVER_INFO_ACK)
                SlurpHelper(new K3p.kmo_server_info_ack(), out outMsg);

            else if (outputIns == K3p.KMO_SERVER_INFO_NACK)
                SlurpHelper(new K3p.kmo_server_info_nack(), out outMsg);

            else if (inCmd is K3p.kpp_get_kws_ticket && outputIns == 0)
                SlurpHelper(new K3p.kmo_get_kws_ticket(), out outMsg);

            else if (inCmd is K3p.kpp_lookup_rec_addr && outputIns == 0)
                SlurpHelper(new K3p.kmo_lookup_rec_addr(), out outMsg);

            else
                throw new Exception("unexpected KMOD instruction " + outputIns);
        }

        /// <summary>
        /// Helper method for Slurp().
        /// </summary>
        private void SlurpHelper(K3pMsg inMsg, out K3pMsg outMsg)
        {
            inMsg.FromElementReader(this);
            outMsg = inMsg;
        }
    }

    /// <summary>
    /// Represent a K3P element contained in a message.
    /// </summary>
    public class K3pElement
    {
        public enum K3pType
        {
            INT,
            STR,
            INS
        }

        private K3pType TypeValue;
        private UInt32 IntValue;
        private byte[] StrValue;

        public void ParseType(byte[] buf)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(buf), Encoding.GetEncoding("iso-8859-1"));
            String t = new String(r.ReadChars(3));
            TypeValue = (K3pType)Enum.Parse(typeof(K3pType), t);
        }

        public void ParseIns(byte[] buf)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(buf), Encoding.GetEncoding("iso-8859-1"));
            String str = new String(r.ReadChars(buf.Length));
            IntValue = Convert.ToUInt32(str, 16);
        }

        /// <summary>
        /// Int in K3p are '>' terminated.
        /// </summary>
        /// <param name="buf">The string containing the integer terminated by a '>'</param>
        public void ParseInt(byte[] buf)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(buf), Encoding.GetEncoding("iso-8859-1"));
            String Str = new String(r.ReadChars(buf.Length - 1));
            IntValue = Convert.ToUInt32(Str);
        }

        public void ParseStr(byte[] buf)
        {
            StrValue = (byte[])buf.Clone();
        }

        public K3pType Type
        {
            get { return TypeValue; }
        }

        /// <summary>
        /// Return the name of the type specified.
        /// </summary>
        public static String GetTypeName(K3pType type)
        {
            switch (type)
            {
                case K3pType.INS: return "instruction";
                case K3pType.INT: return "integer";
                case K3pType.STR: return "string";
                default: return "unknown type";
            }
        }

        public UInt32 Int
        {
            get
            {
                return IntValue;
            }
            set
            {
                IntValue = value;
            }
        }

        public UInt32 Ins
        {
            get
            {
                return IntValue;
            }
            set
            {
                IntValue = value;
            }
        }

        public String Str
        {
            get
            {
                BinaryReader r = new BinaryReader(new MemoryStream(StrValue), Encoding.GetEncoding("iso-8859-1"));
                return new String(r.ReadChars(StrValue.Length));
            }
            set
            {
                MemoryStream s = new MemoryStream();
                BinaryWriter w = new BinaryWriter(s, Encoding.GetEncoding("iso-8859-1"));
                w.Write(value);
                StrValue = s.ToArray();
            }
        }

        public byte[] Bin
        {
            get
            {

                return StrValue;
            }
            set
            {
                StrValue = (byte[])value.Clone();
            }
        }
    }

    /// <summary>
    /// K3P protocol definition.
    /// </summary>
    public class K3p
    {

        /* What version of the protocol does this file specify */
        public const int K3P_VER_MAJOR = 1;
        public const int K3P_VER_MINOR = 8;

        /* TEMPORARY HACK. WILL BE REWORKED.
         * Input: entry_id. // SHOULD BE OTUT STRING.
         * Output: 0 if successful, then bool: otut contained valid or not.
         */
        public const int K3P_CHECK_OTUT = 33;

        /* Instruction 0 means all is OK, query response follow. */
        public const int K3P_COMMAND_OK = 0;

        /* Obtain a ticket authorizing the creation or joining of a workspace. */
        public const int K3P_GET_KWS_TICKET = 40;
        /* Input:  None.
         * Output: Str Ticket (in base 64, for Outlook).
         */
        public class kpp_get_kws_ticket : K3pCmd
        {
            public kpp_get_kws_ticket() : base(K3P_GET_KWS_TICKET) { }
        }
        public class kmo_get_kws_ticket : K3pMsg { public String Ticket; }


        /* Convert Exchange addresses to SMTP addresses. */
        public const int K3P_CONVERT_EXCHANGE_ADDRESS = 41;
        /* Input:  Int Number of addresses.
         *         For each address:
         *           Str Exchange address.
         * Output: Int Number of addresses.
         *         For each address:
         *           Str SMTP address ("" if cannot convert).
         */

        /* Lookup the SMTP addresses specified and determine the key ID and the company
         * name associated to each address, if there is one.
         */
        public const int K3P_LOOKUP_REC_ADDR = 42;
        /* Input:  Int Number of addresses.
         *         For each address:
         *           Str Address.
         * Output: Int Number of addresses.
         *         For each address:
         *           Str Key ID (marshalled in string, because it's 64 bits long).
         *               "" if the user is a non-member.
         *           Str Company name, if the user is a member.
         */
        public class kpp_lookup_rec_addr : K3pCmd
        {
            public kpp_lookup_rec_addr() : base(K3P_LOOKUP_REC_ADDR) { }
            public String[] AddrArray;
        }
        public class kmo_lookup_rec_addr_rec : K3pMsg { public String KeyID; public String OrgName; }
        public class kmo_lookup_rec_addr : K3pMsg { public kmo_lookup_rec_addr_rec[] RecArray; }

        /* Same as K3P_PROCESS_INCOMING, but the decryption email is returned if it was
         * found.
         */
        public const int K3P_PROCESS_INCOMING_EX = 43;
        /* Input:  Same as usual.
         * Output: Same as usual.
         *         Str Email address ("" if none).
         */

        /* Used by the KCD to validate a ticket. */
        public const int K3P_VALIDATE_TICKET = 44;
        /* Input:  Str Ticket.
         *         Str Key ID (marshalled as string).
         * Output: Int Result (0: OK, 1: failed).
         *         Str Error String ("" if none).
         */


        public class k3p_mail_body : K3pMsg
        {
            public enum Body_Type
            {
                K3P_MAIL_BODY_TYPE = 0x4783AF39,
                K3P_MAIL_BODY_TYPE_TEXT = K3P_MAIL_BODY_TYPE + 1,
                K3P_MAIL_BODY_TYPE_HTML = K3P_MAIL_BODY_TYPE + 2,
                K3P_MAIL_BODY_TYPE_TEXT_N_HTML = K3P_MAIL_BODY_TYPE + 3
            }

            public Body_Type type;
            public String text = null;
            public String html = null;
        };

        public class k3p_mail_attachment : K3pMsg
        {
            public enum Mail_Attachment_Type
            {
                K3P_MAIL_ATTACHMENT_TIE = 0x57252924,
                K3P_MAIL_ATTACHMENT_EXPLICIT = K3P_MAIL_ATTACHMENT_TIE + 1,
                K3P_MAIL_ATTACHMENT_IMPLICIT = K3P_MAIL_ATTACHMENT_TIE + 2,
                K3P_MAIL_ATTACHMENT_UNKNOWN = K3P_MAIL_ATTACHMENT_TIE + 3
            };

            public Mail_Attachment_Type tie;

            public UInt32 data_is_file_path;     /* filepath or actual content? */
            public String data;     	/* path to the file or file content. */
            public String name; 	/* name, either a file name or the implicit attachment name. */
            public String encoding;
            public String mime_type;
        };

        public class k3p_otut : K3pMsg
        {
            public enum Kmo_Otut_Status
            {
                KMO_OTUT_STATUS_MAGIC_NUMBER = unchecked((int)0xFAEB9091),
                KMO_OTUT_STATUS_NONE = unchecked(KMO_OTUT_STATUS_MAGIC_NUMBER + 1),
                KMO_OTUT_STATUS_USABLE = unchecked(KMO_OTUT_STATUS_MAGIC_NUMBER + 2),
                KMO_OTUT_STATUS_USED = unchecked(KMO_OTUT_STATUS_MAGIC_NUMBER + 3),
                KMO_OTUT_STATUS_ERROR = unchecked(KMO_OTUT_STATUS_MAGIC_NUMBER + 4)
            };

            public Kmo_Otut_Status status; /* NONE    : no OTUT */
            /* USABLE  : OTUT can be used, time to live in 'msg' */
            /* USED    : the OTUT has been used (date and time are set in 'msg')  */
            /* ERROR   : Unable to send with OTUT (expired, etc.), error message set in 'msg' */

            public String entry_id;    	/* Entry ID of the mail providing the OTUT. */
            public String reply_addr;	/* Reply address associated to the sender. */
            public String msg;	    	/* Message binded to the status. */
        };

        public class k3p_mail : K3pMsg
        {
            public String msg_id;       /* native ID stored by mail client */

            public String recipient_list; /* Recipient list. */

            public String from_name;
            public String from_addr;
            public String to;
            public String cc;
            public String subject;

            public k3p_mail_body body;

            //public UInt32 attachment_nbr; //Implicit by the array
            public k3p_mail_attachment[] attachments;
            public k3p_otut otut;
        };

        /* KPP request IDs */

        public const int KPP_MAGIC_NUMBER = 0x43218765;

        /* Session management */
        public const int KPP_CONNECT_KMO = KPP_MAGIC_NUMBER + 1;   /* with kpp_mua struct */
        public class k3p_connect : K3pCmd
        {
            public k3p_connect() : base(KPP_CONNECT_KMO) { }
            public kpp_mua Product = new kpp_mua();
        };
        public const int KPP_DISCONNECT_KMO = KPP_MAGIC_NUMBER + 2;
        public const int KPP_BEG_SESSION = KPP_MAGIC_NUMBER + 3;
        public class k3p_beg_session : K3pCmd
        {
            public k3p_beg_session() : base(KPP_BEG_SESSION) { }
        }
        public const int KPP_END_SESSION = KPP_MAGIC_NUMBER + 4;
        public class k3p_end_session : K3pCmd
        {
            public k3p_end_session() : base(KPP_END_SESSION) { }
        }

        /* Settings and misc. info */
        const int KPP_IS_KSERVER_INFO_VALID = KPP_MAGIC_NUMBER + 10;   /* use with kpp_server_info struct */
        public class K3pLoginTest : K3pCmd
        {
            public K3pLoginTest() : base(KPP_IS_KSERVER_INFO_VALID) { }
            public kpp_server_info Info = new kpp_server_info();
        };
        const int KPP_SET_KSERVER_INFO = KPP_MAGIC_NUMBER + 11;   /* user info / KPS / KOS */
        public class K3pSetServerInfo : K3pCmd
        {
            public K3pSetServerInfo() : base(KPP_SET_KSERVER_INFO) { }
            public kpp_server_info Info = new kpp_server_info();
        };

        /* Mail packaging requests */
        public const int KPP_SIGN_MAIL = KPP_MAGIC_NUMBER + 20;   /* Return k3p_mail_body */
        public const int KPP_SIGN_N_POD_MAIL = KPP_MAGIC_NUMBER + 21;
        public const int KPP_SIGN_N_ENCRYPT_MAIL = KPP_MAGIC_NUMBER + 22;
        public const int KPP_SIGN_N_ENCRYPT_N_POD_MAIL = KPP_MAGIC_NUMBER + 23;
        public const int KPP_CONFIRM_REQUEST = KPP_MAGIC_NUMBER + 24;
        public const int KPP_USE_PWDS = KPP_MAGIC_NUMBER + 25;   /* nbr recipients + kpp_recipient_pwd array */

        /* Dealing with incoming messages */
        public const int KPP_EVAL_INCOMING = KPP_MAGIC_NUMBER + 30;   /* Evaluation an incoming mail. The result is as follow:
                                                                * 0: Can't happen.
                                                                * 1: Kryptiva mail. Evaluation results follow.
                                                                * 2: Not a Kryptiva mail.
                                                                */

        public const int KPP_PROCESS_INCOMING = KPP_MAGIC_NUMBER + 31;   /* with kpp_mail_process_req */

        public const int KPP_MARK_UNSIGNED_MAIL = KPP_MAGIC_NUMBER + 32;   /* some mails are not signed...ask KMO to remember that.
                                                                * nbr of entries + msg IDs.
                                                                */

        public const int KPP_SET_DISPLAY_PREF = KPP_MAGIC_NUMBER + 33;   /* change the display preference of a Kryptiva mail.
                                                                * input: msg_id, display_pref (0, 1, 2).
                                                                * output: KMO_SET_DISPLAY_PREF_ACK or KMO_SET_DISPLAY_PREF_NACK.
                                                                */

        /* Dealing with stored messages */
        public const int KPP_GET_EVAL_STATUS = KPP_MAGIC_NUMBER + 40;   /* with nbr of entries + msg IDs */
        public const int KPP_GET_STRING_STATUS = KPP_MAGIC_NUMBER + 41;   /* Same input as above. The result is as follow:
                                                                * 0: unknown mail.
                                                                * 1: can't happen (to prevent confusion with
                                                                *                  KPP_GET_EVAL_STATUS codes).
                                                                * 2: not a Kryptiva mail.
                                                                * 3: Kryptiva invalid signature.
                                                                * 4: Kryptiva corrupted mail.
                                                                * 5: Kryptiva signed mail. Sender name string follows.
                                                                * 6: Kryptiva encrypted mail. Sender name string follows.
                                                                * 7: Kryptiva mail with gray zone display preference.
                                                                * 8: Kryptiva mail with unsigned display preference.
                                                                */

        /* Password management */
        public class kpp_email_pwd : K3pMsg
        {
            public String addr;
            public String pwd;
        };

        public const int KPP_GET_EMAIL_PWD = KPP_MAGIC_NUMBER + 50; 	/* input: nb addresses to get the passwords for,
                                                                 *  	  array of addresses.
                                                                 * output: nb_results, array of results: password if 
                                                                 *         found, empty string if not.
                                                                 */
        public const int KPP_GET_ALL_EMAIL_PWD = KPP_MAGIC_NUMBER + 51; 	/* output: number of user passwords in the DB.
                                                                 *         array of kpp_email_pwd.
                                                                 */
        public const int KPP_SET_EMAIL_PWD = KPP_MAGIC_NUMBER + 52; 	/* input: nb passwords to set, kpp_email_pwd containing
                                                                 *        the addr/pwd to set.
                                                                 */
        public const int KPP_REMOVE_EMAIL_PWD = KPP_MAGIC_NUMBER + 53; 	/* input: nb passwords to remove, array of addresses. */


        /* Structs used by KPP to send info to KMO */

        public class kpp_server_info : K3pMsg
        {
            public String kps_login = "";
            public String kps_secret = "";
            public UInt32 secret_is_pwd = 0;
            public String pod_addr = "";

            public String kps_net_addr = "";
            public UInt32 kps_port_num = 443;
            public String kps_ssl_key = "";

            public UInt32 kps_use_proxy = 0;
            public String kps_proxy_net_addr = "";
            public UInt32 kps_proxy_port_num = 0;
            public String kps_proxy_login = "";
            public String kps_proxy_pwd = "";

            public UInt32 kos_use_proxy = 0;
            public String kos_proxy_net_addr = "";
            public UInt32 kos_proxy_port_num = 0;
            public String kos_proxy_login = "";
            public String kos_proxy_pwd = "";
        };

        /* Some MUAs may require special processing because of their limitations */
        public class kpp_mua : K3pMsg
        {
            public enum Kpp_Mua
            {
                KPP_MUA_OUTLOOK = 1,
                KPP_MUA_THUNDERBIRD = 2,
                KPP_MUA_LOTUS_NOTES = 3,
                KPP_MUA_KWM = 4
            };

            /* Product (e.g. 'Outlook') ID. */
            public Kpp_Mua product = Kpp_Mua.KPP_MUA_KWM;

            /* Product version (e.g. '11') ID. */
            public UInt32 version = 1;

            /* Product release string (e.g. 11.0.0.8010). */
            public String release = KwmUtil.KwmVersion;

            /* Plugin major number. */
            public UInt32 kpp_major = K3P_VER_MAJOR;

            /* Plugin minor number. */
            public UInt32 kpp_minor = K3P_VER_MINOR;

            /* This flag is true if the plugin prefers to receive attachments
             * in files rather than on the pipe/socket.
             */
            public UInt32 incoming_attachment_is_file_path = 1;

            public enum Kpp_Lang
            {
                KPP_LANG_EN = 0,
                KPP_LANG_FR = 1
            }
            /* Language used by the user of the plugin. The codes are defined in
             * knp_core_defs.h.
             */
            public Kpp_Lang lang = Kpp_Lang.KPP_LANG_EN;
        };

        public class kpp_recipient_pwd : K3pMsg
        {
            public String recipient;
            public String password;

            public UInt32 give_otut;      /* should the recipient be allowed to respond securely? */
            public UInt32 save_pwd;	 /* remember the default password */
        };

        public class kpp_mail_process_req : K3pMsg
        {
            public k3p_mail mail;

            public UInt32 decrypt;
            public String decryption_pwd;
            public UInt32 save_pwd;

            public UInt32 ack_pod;
            /* This is for returning to the sender on a PoD. It is non-authoritative,
               obviously. So the recipient could fake it. Realistically, though, it
               would take *all* recipients to fake it to potentially confuse the
               sender, who, either way, will be able to determine something fishy is
               going on because of the inconsistencies between the recipient list and
               the PoDs recieved. All that keeping in mind that KOS can be authoritative
               on PoD requestors' IP addresses, if nothing else ... */
            public String recipient_mail_address;
        };

        /* KMO responses to KPP */

        public const int KMO_MAGIC_NUMBER = 0x12349876;

        /* Session management */
        public const int KMO_COGITO_ERGO_SUM = KMO_MAGIC_NUMBER + 1;   /* with kmo_tool_info struct */
        public const int KMO_INVALID_REQ = KMO_MAGIC_NUMBER + 2;   /* spreken zie deutsch? */
        public const int KMO_INVALID_CONFIG = KMO_MAGIC_NUMBER + 3;   /* sorry joe, trying configuring first */
        public class kmo_invalid_config : K3pMsg { }
        public const int KMO_SERVER_ERROR = KMO_MAGIC_NUMBER + 4;   /* with kmo_server_error string */

        /* Responses to settings and misc. info reqs. */
        public const int KMO_SERVER_INFO_ACK = KMO_MAGIC_NUMBER + 10;   /* with encrypted password */
        public class kmo_server_info_ack : K3pMsg { public String Token; }
        public const int KMO_SERVER_INFO_NACK = KMO_MAGIC_NUMBER + 11;   /* with error string */
        public class kmo_server_info_nack : K3pMsg { public String Error; }

        /* Responses to mail packaging reqs. */
        public const int KMO_PACK_ACK = KMO_MAGIC_NUMBER + 20;
        public const int KMO_PACK_NACK = KMO_MAGIC_NUMBER + 21;   /* with kmo_pack_explain */
        public const int KMO_PACK_CONFIRM = KMO_MAGIC_NUMBER + 22;   /* with kmo_pack_explain */
        public const int KMO_PACK_ERROR = KMO_MAGIC_NUMBER + 23;   /* misc. */
        public const int KMO_NO_RECIPIENT_PUB_KEY = KMO_MAGIC_NUMBER + 24;   /* with nbr recipients + array of kpp_recipient_pwd */
        public const int KMO_INVALID_OTUT = KMO_MAGIC_NUMBER + 25;   /* the OTUT provided isn't usable */

        /* Responses to incoming and stored mail reqs. */
        public const int KMO_EVAL_STATUS = KMO_MAGIC_NUMBER + 30;   /* with array(status + (kmo_eval_res iff status == 1)) */
        public const int KMO_STRING_STATUS = KMO_MAGIC_NUMBER + 31; 	 /* as described above. */
        public const int KMO_PROCESS_ACK = KMO_MAGIC_NUMBER + 32;   /* with k3p_mail struct where only body and attach. are set */
        public const int KMO_PROCESS_NACK = KMO_MAGIC_NUMBER + 33;   /* with kmo_process_nack struct */
        public const int KMO_MARK_UNSIGNED_MAIL = KMO_MAGIC_NUMBER + 34;   /* ACK for KPP_MARK_UNSIGNED_MAIL */
        public const int KMO_SET_DISPLAY_PREF_ACK = KMO_MAGIC_NUMBER + 35;
        public const int KMO_SET_DISPLAY_PREF_NACK = KMO_MAGIC_NUMBER + 36;

        public const int KMO_PWD_ACK = KMO_MAGIC_NUMBER + 50;

        /* Upgrade message. */
        public const int KMO_MUST_UPGRADE = KMO_MAGIC_NUMBER + 60;   /* with one of the values below that indicates why the update
                                                          * is needed
                                                          */
        public const int KMO_UPGRADE_SIG = 1;  	    	    	 /* can't handle mail signature */
        public const int KMO_UPGRADE_KOS = 2;  	    	    	 /* KOS refuses to speak to us */
        public const int KMO_UPGRADE_KPS = 3;  	    	    	 /* KPS too old, cannot do requested work */
        public class kmo_must_upgrade : K3pMsg
        {
            public UInt32 what;
        }

        /* Explanation after received KMO_PROCESS_NACK */
        public class kmo_process_nack : K3pMsg
        {
            public enum Process_Nack_Reason
            {
                KMO_PROCESS_NACK_MAGIC_NUMBER = 0x531AB246,
                KMO_PROCESS_NACK_POD_ERROR = KMO_PROCESS_NACK_MAGIC_NUMBER + 1,  /* KOS can't deliver PoD */
                KMO_PROCESS_NACK_PWD_ERROR = KMO_PROCESS_NACK_MAGIC_NUMBER + 2,  /* wrong password */
                KMO_PROCESS_NACK_DECRYPT_PERM_FAIL = KMO_PROCESS_NACK_MAGIC_NUMBER + 3,  /* not authorized to decrypt this message */
                KMO_PROCESS_NACK_MISC_ERROR = KMO_PROCESS_NACK_MAGIC_NUMBER + 4   /* miscellaneous error */
            };
            public Process_Nack_Reason error;
            public String error_msg = null;   /* Error message set when a miscellaneous error occurred */
        };

        public class kmo_tool_info : K3pMsg
        {
            public String sig_marker;
            public String kmo_version;
            public String k3p_version;
        };

        public class kmo_server_error : K3pMsg
        {
            /* Designation for server with which there was an error */
            public enum Kmo_Sid
            {
                KMO_SID_MAGIC_NUMBER = unchecked((int)((0x8724U << 16) + (1 << 8))),
                KMO_SID_KPS = KMO_SID_MAGIC_NUMBER + 1,
                KMO_SID_OPS = KMO_SID_MAGIC_NUMBER + 2,
                KMO_SID_OUS = KMO_SID_MAGIC_NUMBER + 3,
                KMO_SID_OTS = KMO_SID_MAGIC_NUMBER + 4,
                KMO_SID_IKS = KMO_SID_MAGIC_NUMBER + 5,
                KMO_SID_EKS = KMO_SID_MAGIC_NUMBER + 6
            };
            public Kmo_Sid sid;       	    /* server ID */

            public enum Kmo_Serror
            {
                KMO_SERROR_MAGIC_NUMBER = unchecked((int)0X8FBA3CDE),
                KMO_SERROR_MISC = KMO_SERROR_MAGIC_NUMBER + 1,
                KMO_SERROR_TIMEOUT = KMO_SERROR_MAGIC_NUMBER + 2,
                KMO_SERROR_UNREACHABLE = KMO_SERROR_MAGIC_NUMBER + 3,
                KMO_SERROR_CRIT_MSG = KMO_SERROR_MAGIC_NUMBER + 4
            };
            public Kmo_Serror error;
            public String message = null;
        };

        public class kmo_eval_res_attachment : K3pMsg
        {
            public enum Kmo_Eval_Attachment
            {
                KMO_EVAL_ATTACHMENT_MAGIC_NUMBER = 0X65920424,
                KMO_EVAL_ATTACHMENT_DROPPED = KMO_EVAL_ATTACHMENT_MAGIC_NUMBER + 1,
                KMO_EVAL_ATTACHMENT_INTACT = KMO_EVAL_ATTACHMENT_MAGIC_NUMBER + 2,
                KMO_EVAL_ATTACHMENT_MODIFIED = KMO_EVAL_ATTACHMENT_MAGIC_NUMBER + 3,
                KMO_EVAL_ATTACHMENT_INJECTED = KMO_EVAL_ATTACHMENT_MAGIC_NUMBER + 4,
                KMO_EVAL_ATTACHMENT_ERROR = KMO_EVAL_ATTACHMENT_MAGIC_NUMBER + 5
            };

            public String name;	/* Name of the attachment, if any. */
            public Kmo_Eval_Attachment status;	/* Status of the attachment, as described above. */
        };

        public class kmo_eval_res : K3pMsg
        {
            public enum Kmo_Display_Form
            {
                KMO_DISPLAY_GRAY_ZONE = 0,
                KMO_DISPLAY_KRYPTIVA = 1,
                KMO_DISPLAY_UNSIGNED = 2
            };

            public Kmo_Display_Form display_pref;

            public UInt32 string_status;		/* String status code as defined in KPP_GET_STRING_STATUS. */

            public UInt32 sig_valid;             /* Mail contains a valid genuine Kryptiva signature.
                                             * Note that if this is 0, the following fields contain
                                             * invalid info that should not be interpreted (statuses might
                                             * be 0 and strings might be empty).
                                             */

            public String sig_msg;  	/* If the signature is not valid, this message explains why. */


            public enum Kmo_Orig_Packaging
            {
                KMO_SIGNED_MASK = (1 << 0),  //00000001
                KMO_ENCRYPTED_MASK = (1 << 1),  //00000010
                KMO_ENCRYPTED_WITH_PWD_MASK = (1 << 2),  //00000100
                KMO_REQUIRED_POD_MASK = (1 << 3),  //00001000
                KMO_CONTAINED_OTUT_MASK = (1 << 4),  //00010000
            };
            public Kmo_Orig_Packaging original_packaging;    /* bit fields */

            public String subscriber_name;  /* who's the authoritative sender? */

            public enum Kmo_Field_Status
            {
                KMO_FIELD_STATUS_MAGIC_NUMBER = unchecked((int)0xFD4812ED),
                KMO_FIELD_STATUS_ABSENT = KMO_FIELD_STATUS_MAGIC_NUMBER + 1,
                KMO_FIELD_STATUS_INTACT = KMO_FIELD_STATUS_MAGIC_NUMBER + 2,
                KMO_FIELD_STATUS_CHANGED = KMO_FIELD_STATUS_MAGIC_NUMBER + 3,
            };

            public Kmo_Field_Status from_name_status;
            public Kmo_Field_Status from_addr_status;
            public Kmo_Field_Status to_status;
            public Kmo_Field_Status cc_status;
            public Kmo_Field_Status subject_status;
            public Kmo_Field_Status body_text_status;
            public Kmo_Field_Status body_html_status;

            //public UInt32 attachment_nbr; // Implicit
            public kmo_eval_res_attachment[] attachments;

            public enum Kmo_Decruption_Status
            {
                KMO_DECRYPTION_STATUS_MAGIC_NUMBER = unchecked((int)0xFED123AB),
                KMO_DECRYPTION_STATUS_NONE = KMO_DECRYPTION_STATUS_MAGIC_NUMBER + 1,
                KMO_DECRYPTION_STATUS_ENCRYPTED = KMO_DECRYPTION_STATUS_MAGIC_NUMBER + 2,
                KMO_DECRYPTION_STATUS_ENCRYPTED_WITH_PWD = KMO_DECRYPTION_STATUS_MAGIC_NUMBER + 3,
                KMO_DECRYPTION_STATUS_DECRYPTED = KMO_DECRYPTION_STATUS_MAGIC_NUMBER + 4,
                KMO_DECRYPTION_STATUS_ERROR = KMO_DECRYPTION_STATUS_MAGIC_NUMBER + 5,
            };

            public Kmo_Decruption_Status encryption_status;
            /* NONE : the mail is not encrypted   */
            /* ENCRYPTED : mail is encrypted, but it hasn't been tried to be decrypt yet */
            /* ENCRYPTED_WITH_PWD : a password is required to decrypt the mail           */
            /* DECRYPTED : mail has already been decrypted and the  */
            /*            decryption key is saved in the DB         */
            /* ERROR : mail has already been tried to decrypt,      */
            /*         but the mail is corrupted.                   */
            public String decryption_error_msg; /* explanation of the decryption status */

            public String default_pwd;  /* Last password provided to decrypt mails from this message sender. */

            public enum Kmo_Pod_Status
            {
                KMO_POD_STATUS_MAGIC_NUMBER = unchecked((int)0xCBA987EF),
                KMO_POD_STATUS_NONE = KMO_POD_STATUS_MAGIC_NUMBER + 1,
                KMO_POD_STATUS_UNDELIVERED = KMO_POD_STATUS_MAGIC_NUMBER + 2,
                KMO_POD_STATUS_DELIVERED = KMO_POD_STATUS_MAGIC_NUMBER + 3,
                KMO_POD_STATUS_ERROR = KMO_POD_STATUS_MAGIC_NUMBER + 4,
            };

            public Kmo_Pod_Status pod_status;
            /* NONE        : no PoD to send */
            /* UNDELIVERED : a PoD must be delivered */
            /* DELIVERED   : PoD has been delivered successfully (Date and time are set in pod_msg) */
            /* ERROR       : Unable to send PoD - mail corrupted  */
            public String pod_msg;  /* Explanation of the error or time of */
            /* the PoD has been sent successfully, */

            public k3p_otut otut;  /* OTUT status */
        };

        public class kmo_pack_explain : K3pMsg
        {
            public enum Kmo_Pack_Expl_Type
            {
                KMO_PACK_EXPL_MAGIC_NUMBER = unchecked((int)0x820994AF),
                KMO_PACK_EXPL_UNSPECIFIED = KMO_PACK_EXPL_MAGIC_NUMBER + 1,
                KMO_PACK_EXPL_SUSPECT_SPAM = KMO_PACK_EXPL_MAGIC_NUMBER + 2,
                KMO_PACK_EXPL_SUSPECT_VIRUS = KMO_PACK_EXPL_MAGIC_NUMBER + 3,
                KMO_PACK_EXPL_SHOULD_ENCRYPT = KMO_PACK_EXPL_MAGIC_NUMBER + 4,
                KMO_PACK_EXPL_SHOULD_POD = KMO_PACK_EXPL_MAGIC_NUMBER + 5,
                KMO_PACK_EXPL_SHOULD_ENCRYPT_N_POD = KMO_PACK_EXPL_MAGIC_NUMBER + 6,
                KMO_PACK_EXPL_CUSTOM = KMO_PACK_EXPL_MAGIC_NUMBER + 7,
            };
            public Kmo_Pack_Expl_Type type;

            public String text;
            public String captcha;
        };
    }
}