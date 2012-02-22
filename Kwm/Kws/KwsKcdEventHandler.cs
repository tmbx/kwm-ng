using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;

namespace kwm
{
    /// <summary>
    /// Handle the workspace events received from the KCD that do not concern 
    /// the applications.
    /// </summary>
    public class KwsKcdEventHandler
    {
        /// <summary>
        /// Reference to the workspace.
        /// </summary>
        private Workspace m_kws;

        public void Relink(Workspace kws)
        {
            m_kws = kws;
        }

        /// <summary>
        /// Handle an ANP event.
        /// </summary>
        public KwsAnpEventStatus HandleAnpEvent(AnpMsg msg)
        {
            // Dispatch.
            UInt32 type = msg.Type;
            if (type == KAnp.KANP_EVT_KWS_CREATED) return HandleKwsCreatedEvent(msg);
            else if (type == KAnp.KANP_EVT_KWS_INVITED) return HandleKwsInvitationEvent(msg);
            else if (type == KAnp.KANP_EVT_KWS_USER_REGISTERED) return HandleUserRegisteredEvent(msg);
            else if (type == KAnp.KANP_EVT_KWS_DELETED) return HandleKwsDeletedEvent();
            else if (type == KAnp.KANP_EVT_KWS_LOG_OUT) return HandleKwsLogOut(msg);
            else if (type == KAnp.KANP_EVT_KWS_PROP_CHANGE) return HandleKwsPropChange(msg);
            else return KwsAnpEventStatus.Unprocessed;
        }

        private KwsAnpEventStatus HandleKwsCreatedEvent(AnpMsg msg)
        {
            KwsCredentials creds = m_kws.Cd.Credentials;

            // Add the creator to the user list.
            KwsUser user = new KwsUser();
            user.UserID = msg.Elements[2].UInt32;
            user.InvitationDate = msg.Elements[1].UInt64;
            user.AdminName = msg.Elements[3].String;
            user.EmailAddress = msg.Elements[4].String;
            user.OrgName = msg.Elements[msg.Minor <= 2 ? 7 : 5].String;
            user.AdminFlag = true;
            user.ManagerFlag = true;
            user.RegisterFlag = true;
            m_kws.Cd.UserInfo.UserTree[user.UserID] = user;

            // Update the workspace data.
            if (msg.Minor <= 2)
            {
                creds.SecureFlag = true;
            }

            if (msg.Minor >= 3)
            {
                creds.KwsName = msg.Elements[6].String;
                creds.Flags = msg.Elements[7].UInt32;
                creds.KwmoAddress = msg.Elements[8].String;
            }

            m_kws.OnStateChange(WmStateChange.Permanent);
            return KwsAnpEventStatus.Processed;
        }

        private KwsAnpEventStatus HandleKwsInvitationEvent(AnpMsg msg)
        {
            UInt32 nbUser = msg.Elements[msg.Minor <= 2 ? 2 : 3].UInt32;

            // This is not supposed to happen, unless in the case of a broken
            // KWM. Indeed, the server does not enforce any kind of restriction
            // regarding the number of invitees in an INVITE command. If a KWM
            // sends such a command with no invitees, the server will fire an
            // empty INVITE event.
            if (nbUser < 1) return KwsAnpEventStatus.Processed;

            List<KwsUser> users = new List<KwsUser>();

            // Add the users in the user list.
            int j = (msg.Minor <= 2) ? 3 : 4;
            for (int i = 0; i < nbUser; i++)
            {
                KwsUser user = new KwsUser();
                user.UserID = msg.Elements[j++].UInt32;
                user.InvitationDate = msg.Elements[1].UInt64;
                if (msg.Minor >= 3) user.InvitedBy = msg.Elements[2].UInt32;
                user.AdminName = msg.Elements[j++].String;
                user.EmailAddress = msg.Elements[j++].String;
                if (msg.Minor <= 2) j += 2;
                user.OrgName = msg.Elements[j++].String;
                users.Add(user);
                m_kws.Cd.UserInfo.UserTree[user.UserID] = user;
            }

            m_kws.OnStateChange(WmStateChange.Permanent);
            return KwsAnpEventStatus.Processed;
        }

        private KwsAnpEventStatus HandleUserRegisteredEvent(AnpMsg msg)
        {
            UInt32 userID = msg.Elements[2].UInt32;
            String userName = msg.Elements[3].String;

            KwsUser user = m_kws.Cd.UserInfo.GetNonVirtualUserByID(userID);
            if (user == null) throw new Exception("no such user");
            user.UserName = userName;

            m_kws.OnStateChange(WmStateChange.Permanent);
            return KwsAnpEventStatus.Processed;
        }

        private KwsAnpEventStatus HandleKwsDeletedEvent()
        {
            m_kws.Cd.KcdState.LoginResult = KwsLoginResult.DeletedKws;
            m_kws.Cd.KcdState.LoginResultString = "the " + KwmStrings.Kws + " has been deleted";
            m_kws.OnStateChange(WmStateChange.Permanent);
            m_kws.Sm.RequestTaskSwitch(KwsTask.WorkOffline);
            return KwsAnpEventStatus.Processed;
        }

        private KwsAnpEventStatus HandleKwsLogOut(AnpMsg msg)
        {
            m_kws.Cd.KcdState.LoginResult = m_kws.KcdLoginHandler.TranslateKcdLoginStatusCode(msg.Elements[2].UInt32);
            m_kws.Cd.KcdState.LoginResultString = msg.Elements[3].String;
            m_kws.OnStateChange(WmStateChange.Permanent);
            m_kws.Sm.RequestTaskSwitch(KwsTask.WorkOffline);
            return KwsAnpEventStatus.Processed;
        }

        private KwsAnpEventStatus HandleKwsPropChange(AnpMsg msg)
        {
            KwsCredentials creds = m_kws.Cd.Credentials;
            KwsUserInfo userInfo = m_kws.Cd.UserInfo;

            int i = 3;
            UInt32 nbChange = msg.Elements[i++].UInt32;

            for (UInt32 j = 0; j < nbChange; j++)
            {
                UInt32 type = msg.Elements[i++].UInt32;

                if (type == KAnp.KANP_PROP_KWS_NAME)
                {
                    creds.KwsName = msg.Elements[i++].String;
                }

                else if (type == KAnp.KANP_PROP_KWS_FLAGS)
                    creds.Flags = msg.Elements[i++].UInt32;

                else
                {
                    KwsUser user = userInfo.GetNonVirtualUserByID(msg.Elements[i++].UInt32);
                    if (user == null) throw new Exception("no such user");

                    if (type == KAnp.KANP_PROP_USER_NAME_ADMIN)
                        user.AdminName = msg.Elements[i++].String;

                    else if (type == KAnp.KANP_PROP_USER_NAME_USER)
                        user.UserName = msg.Elements[i++].String;

                    else if (type == KAnp.KANP_PROP_USER_FLAGS)
                        user.Flags = msg.Elements[i++].UInt32;

                    else throw new Exception("invalid user property type");
                }
            }

            m_kws.OnStateChange(WmStateChange.Permanent);
            return KwsAnpEventStatus.Processed;
        }
    }
}