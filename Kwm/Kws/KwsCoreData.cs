using kcslib;
using kwmlib;
using System;
using System.Runtime.Serialization;
using System.Collections.Generic;

namespace kwm
{
    /// <summary>
    /// Processing status of an ANP event. These statuses are used in the 
    /// database.
    /// </summary>
    public enum KwsAnpEventStatus
    {
        /// <summary>
        /// The event has not been processed yet.
        /// </summary>
        Unprocessed = 0,

        /// <summary>
        /// The event was processed successfully.
        /// </summary>
        Processed
    }

    /// <summary>
    /// Privilege level associated to a user.
    /// </summary>
    public enum KwsUserPrivLevel
    {
        /// <summary>
        /// The user needs no special permissions.
        /// </summary>
        User,

        /// <summary>
        /// The user must be a workspace manager.
        /// </summary>
        Manager,

        /// <summary>
        /// The user must be a workspace administrator.
        /// </summary>
        Admin,

        /// <summary>
        /// The user must be a system administrator.
        /// </summary>
        Root
    }

    /// <summary>
    /// Notification sent by a workspace state machine.
    /// </summary>
    public abstract class KwsSmNotif : EventArgs
    {
        /// <summary>
        /// Workspace associated to the notification.
        /// </summary>
        public Workspace Kws;

        public KwsSmNotif(Workspace kws)
        {
            Kws = kws;
        }
    }

    /// <summary>
    /// The KCD connection status has changed.
    /// </summary>
    public class KwsSmNotifKcdConn : KwsSmNotif
    {
        /// <summary>
        /// Connection status.
        /// </summary>
        public KcdConnStatus Status;

        /// <summary>
        /// Exception that caused the connection to be lost, if any.
        /// </summary>
        public Exception Ex;

        public KwsSmNotifKcdConn(Workspace kws, KcdConnStatus status, Exception ex)
            : base(kws)
        {
            Status = status;
            Ex = ex;
        }
    }

    /// <summary>
    /// The workspace login status has changed.
    /// </summary>
    public class KwsSmNotifKcdLogin : KwsSmNotif
    {
        /// <summary>
        /// Login status.
        /// </summary>
        public KwsLoginStatus Status;

        /// <summary>
        /// Exception that caused the login to fail, if any.
        /// </summary>
        public Exception Ex;

        public KwsSmNotifKcdLogin(Workspace kws, KwsLoginStatus status, Exception ex)
            : base(kws)
        {
            Status = status;
            Ex = ex;
        }
    }

    /// <summary>
    /// The workspace has applied all KCD events.
    /// </summary>
    public class KwsSmNotifKcdEventUpToDate : KwsSmNotif
    {
        public KwsSmNotifKcdEventUpToDate(Workspace kws)
            : base(kws)
        { }
    }

    /// <summary>
    /// A task switch has occurred.
    /// </summary>
    public class KwsSmNotifTaskSwitch : KwsSmNotif
    {
        /// <summary>
        /// Task associated to the notification, if any.
        /// </summary>
        public KwsTask Task;

        /// <summary>
        /// Exception that caused the task switch to occur, if any.
        /// </summary>
        public Exception Ex;

        public KwsSmNotifTaskSwitch(Workspace kws, KwsTask task, Exception ex)
            : base(kws)
        {
            Task = task;
            Ex = ex;
        }
    }

    /// <summary>
    /// The application status has changed.
    /// </summary>
    public class KwsSmNotifApp : KwsSmNotif
    {
        /// <summary>
        /// Status of the application.
        /// </summary>
        public KwsAppStatus Status;

        public KwsSmNotifApp(Workspace kws, KwsAppStatus status)
            : base(kws)
        {
            Status = status;
        }
    }

    /// <summary>
    /// Represent a user in a workspace.
    /// 
    /// IMPORTANT: do NOT compare users by object pointers. Use the user ID
    /// do to so. The user objects can change dynamically.
    /// </summary>
    [KwmSerializable]
    public class KwsUser
    {
        /// <summary>
        /// ID of the user.
        /// </summary>
        public UInt32 UserID = 0;

        /// <summary>
        /// Date at which the user was added.
        /// </summary>
        public UInt64 InvitationDate = 0;

        /// <summary>
        /// ID of the inviting user. 0 if none.
        /// </summary>
        public UInt32 InvitedBy = 0;

        /// <summary>
        /// Name given by the workspace administrator.
        /// </summary>
        public String AdminName = "";

        /// <summary>
        /// Name given by the user himself.
        /// </summary>
        public String UserName = "";

        /// <summary>
        /// Email address of the user, if any.
        /// </summary>
        public String EmailAddress = "";

        /// <summary>
        /// Organization name, if the user is a member.
        /// </summary>
        public String OrgName = "";

        /// <summary>
        /// KCD flags of the user.
        /// </summary>
        public UInt32 Flags = 0;

        /// <summary>
        /// True if the user is a virtual user. A virtual user is a user for 
        /// which no invitation event is associated. The root user is a virtual
        /// user. The KWM user is also a virtual user if its invitation event 
        /// was not yet processed.
        /// </summary>
        [NonSerialized, NonReplicated]
        public bool VirtualFlag = false;

        [NonReplicated]
        public bool AdminFlag
        {
            get { return (Flags & KAnp.KANP_USER_FLAG_ADMIN) > 0; }
            set { SetFlagValue(KAnp.KANP_USER_FLAG_ADMIN, value); }
        }

        [NonReplicated]
        public bool ManagerFlag
        {
            get { return (Flags & KAnp.KANP_USER_FLAG_MANAGER) > 0; }
            set { SetFlagValue(KAnp.KANP_USER_FLAG_MANAGER, value); }
        }

        [NonReplicated]
        public bool RegisterFlag
        {
            get { return (Flags & KAnp.KANP_USER_FLAG_REGISTER) > 0; }
            set { SetFlagValue(KAnp.KANP_USER_FLAG_REGISTER, value); }
        }

        [NonReplicated]
        public bool LockFlag
        {
            get { return (Flags & KAnp.KANP_USER_FLAG_LOCK) > 0; }
            set { SetFlagValue(KAnp.KANP_USER_FLAG_LOCK, value); }
        }

        [NonReplicated]
        public bool BanFlag
        {
            get { return (Flags & KAnp.KANP_USER_FLAG_BAN) > 0; }
            set { SetFlagValue(KAnp.KANP_USER_FLAG_BAN, value); }
        }

        /// <summary>
        /// Return the privilege level of the user.
        /// </summary>
        [NonReplicated]
        public KwsUserPrivLevel PrivLevel
        {
            get
            {
                if (UserID == 0) return KwsUserPrivLevel.Root;
                if (AdminFlag) return KwsUserPrivLevel.Admin;
                if (ManagerFlag) return KwsUserPrivLevel.Manager;
                return KwsUserPrivLevel.User;
            }
        }

        /// <summary>
        /// KWM flags of the user.
        /// </summary>
        public KwsUserKwmFlag KwmFlags
        {
            get
            {
                return 0;
            }
        }

        /// <summary>
        /// Helper method to set or clear a user flag.
        /// </summary>
        private void SetFlagValue(UInt32 flag, bool value)
        {
            if (value) Flags |= flag;
            else Flags &= ~flag;
        }


        // FIXME: flush the stuff below if it isn't being used.

        /// <summary>
        /// Get the username to display in the UI, without the user's email address (unless no
        /// username exists).
        /// </summary>
        [NonReplicated]
        public String UiSimpleName
        {
            get
            {
                if (AdminName != "") return AdminName;
                if (UserName != "") return UserName;
                return EmailAddress;
            }
        }

        /// <summary>
        /// Get the given name of the user (prénom). If no AdminName or UserName is
        /// set, use the left part of the email address.
        /// </summary>
        [NonReplicated]
        public String UiSimpleGivenName
        {
            get
            {
                // Give priority to AdminName.
                String name = AdminName != "" ? AdminName : UserName;

                // Try to get a valid given name.
                name = GetGivenName(name);

                if (name == "")
                    return GetEmailAddrLeftPart(EmailAddress);
                else
                    return name;
            }
        }

        /// <summary>
        /// Return the first characters of the given name until a space
        /// is found.
        /// </summary>
        private String GetGivenName(String name)
        {
            String[] splitted = name.Split(new char[] { ' ' });
            if (splitted.Length > 0) return splitted[0];
            else return "";
        }

        /// <summary>
        /// Return the left part of an email address, the entire address
        /// if any problem occurs.
        /// </summary>
        private String GetEmailAddrLeftPart(String addr)
        {
            String[] splitted = addr.Split(new char[] { '@' });
            if (splitted.Length > 0) return splitted[0];
            else return addr;
        }

        /// <summary>
        /// Get the username to display in the UI, with its email address appended. If no
        /// username is present, return the email address only.
        /// </summary>
        [NonReplicated]
        public String UiFullName
        {
            get
            {
                if (AdminName == "" && UserName == "") return EmailAddress;
                if (UiSimpleName == EmailAddress) return EmailAddress;
                return UiSimpleName + " (" + EmailAddress + ")";
            }
        }

        /// <summary>
        /// Get the KwsUser description text.
        /// </summary>
        [NonReplicated]
        public String UiTooltipText
        {
            get
            {
                if (UiSimpleName == EmailAddress) return EmailAddress;
                return UiSimpleName + Environment.NewLine + EmailAddress;
            }
        }

        /// <summary>
        /// Return true if the user has an administrative name set.
        /// </summary>
        public bool HasAdminName()
        {
            return AdminName != "";
        }
    }

    /// <summary>
    /// This class is a pure data store that contains information about the
    /// ANP data received from the KCD.
    /// </summary>
    [KwmSerializable]
    public class KwsKcdState
    {
        /// <summary>
        /// ID of the last ANP event received. This is 0 if no event has been
        /// received yet.
        /// </summary>
        public UInt64 LastReceivedEventId = 0;

        /// <summary>
        /// Number of unprocessed ANP events lingering in the DB.
        /// </summary>
        public UInt64 NbUnprocessedEvent = 0;

        /// <summary>
        /// ID of the latest event available on the KCD when the workspace
        /// logs in. This is 0 if no event is available on the KCD.
        /// </summary>
        [NonSerialized]
        public UInt64 LoginLatestEventId = 0;

        /// <summary>
        /// Status of the login with the KCD.
        /// </summary>
        [NonSerialized]
        public KwsLoginStatus LoginStatus = KwsLoginStatus.LoggedOut;

        /// <summary>
        /// Outcome of the last login attempt of the workspace.
        /// </summary>
        public KwsLoginResult LoginResult = KwsLoginResult.None;

        /// <summary>
        /// String describing the login result.
        /// </summary>
        public String LoginResultString = "";

        /// <summary>
        /// True if the KCD has told us that the user has a password.
        /// </summary>
        [NonSerialized]
        public bool PwdPresentFlag = false;

        /// <summary>
        /// True if the user needs to supply the password to log in the
        /// workspace.
        /// </summary>
        [NonSerialized]
        public bool PwdRequiredFlag = false;

        /// <summary>
        /// This flag is false if the task is not WorkOnline or WorkOffline. 
        /// If the task is work online, the flag is true if all the events
        /// available on the KCD have been received and applied. If the task
        /// is work offline, the flag is true if all the events stored in the
        /// database have been applied.
        /// </summary>
        [NonSerialized]
        public bool KcdEventUpToDateFlag = false;

        /// <summary>
        /// Reset some fields when the workspace logs out.
        /// </summary>
        public void ResetOnLogout()
        {
            LoginStatus = KwsLoginStatus.LoggedOut;
            LoginLatestEventId = 0;
            PwdPresentFlag = false;
            PwdRequiredFlag = false;
            KcdEventUpToDateFlag = false;
        }
    }

    /// <summary>
    /// Workspace credentials.
    /// </summary>
    [KwmSerializable]
    public class KwsCredentials
    {
        /// <summary>
        /// Current serialization version when exported in a file.
        /// </summary>
        public const Int32 ExportVersion = 4;

        /// <summary>
        /// Address of the KCD server.
        /// </summary>
        public String KcdAddress;

        /// <summary>
        /// Address of the KWMO server.
        /// </summary>
        public String KwmoAddress = "";

        /// <summary>
        /// External workspace ID.
        /// </summary>
        public UInt64 ExternalID;

        /// <summary>
        /// Email ID associated to this workspace.
        /// </summary>
        public String EmailID = "";

        /// <summary>
        /// Name of the workspace.
        /// </summary>
        public String KwsName = "";

        /// <summary>
        /// Name of the user using this workspace. This field should only be
        /// used when the KWM user is a virtual user.
        public String UserName = "";

        /// <summary>
        /// Email address of the user using this workspace. This field should 
        /// only be used when the KWM user is a virtual user.
        /// </summary>
        public String UserEmailAddress = "";

        /// <summary>
        /// Name of the person who has invited the user, if any. This field 
        /// should only be used when the KWM user is a virtual user.
        /// </summary>
        public String InviterName = "";

        /// <summary>
        /// Email address of the person who has invited the user, if any. This 
        /// field should only be used when the KWM user is a virtual user.
        /// </summary>
        public String InviterEmailAddress = "";

        /// <summary>
        /// ID of the user.
        /// </summary>
        [NonReplicated]
        public UInt32 UserID;

        /// <summary>
        /// Login ticket.
        /// </summary>
        [NonReplicated]
        public byte[] Ticket = null;

        /// <summary>
        /// Login password.
        /// </summary>
        public String Pwd = "";

        /// <summary>
        /// KCD flags of the workspace.
        /// </summary>
        [NonReplicated]
        public UInt32 Flags = 0;

        /// <summary>
        /// Parent folder path.
        /// </summary>
        public String FolderPath = "";

        /// <summary>
        /// Binary blob used by ET.
        /// </summary>
        public byte[] EtBlob;

        public KcdIdentifier KcdID { get { return new KcdIdentifier(KcdAddress, 443) ; } }

        [NonReplicated]
        public bool PublicFlag
        {
            get { return (Flags & KAnp.KANP_KWS_FLAG_PUBLIC) > 0; }
            set { SetFlagValue(KAnp.KANP_KWS_FLAG_PUBLIC, value); }
        }

        [NonReplicated]
        public bool FreezeFlag
        {
            get { return (Flags & KAnp.KANP_KWS_FLAG_FREEZE) > 0; }
            set { SetFlagValue(KAnp.KANP_KWS_FLAG_FREEZE, value); }
        }

        [NonReplicated]
        public bool DeepFreezeFlag
        {
            get { return (Flags & KAnp.KANP_KWS_FLAG_DEEP_FREEZE) > 0; }
            set { SetFlagValue(KAnp.KANP_KWS_FLAG_DEEP_FREEZE, value); }
        }

        [NonReplicated]
        public bool ThinKfsFlag
        {
            get { return (Flags & KAnp.KANP_KWS_FLAG_THIN_KFS) > 0; }
            set { SetFlagValue(KAnp.KANP_KWS_FLAG_THIN_KFS, value); }
        }

        [NonReplicated]
        public bool SecureFlag
        {
            get { return (Flags & KAnp.KANP_KWS_FLAG_SECURE) > 0; }
            set { SetFlagValue(KAnp.KANP_KWS_FLAG_SECURE, value); }
        }

        /// <summary>
        /// Clone this object.
        /// </summary>
        public KwsCredentials Clone()
        {
            KwsCredentials c = (KwsCredentials)MemberwiseClone();
            if (c.Ticket != null) c.Ticket = (byte[])c.Ticket.Clone();
            if (c.EtBlob != null) c.EtBlob = (byte[])c.EtBlob.Clone();
            return c;
        }

        /// <summary>
        /// Helper method to set or clear a workspace flag.
        /// </summary>
        private void SetFlagValue(UInt32 flag, bool value)
        {
            if (value) Flags |= flag;
            else Flags &= ~flag;
        }
    }

    /// <summary>
    /// This class contains information about the users of a workspace.
    /// </summary>
    [KwmSerializable]
    public class KwsUserInfo
    {
        /// <summary>
        /// Reference to the workspace core data.
        /// </summary>
        private KwsCoreData m_cd;

        /// <summary>
        /// Tree of non-virtual users indexed by user ID.
        /// </summary>
        public SortedDictionary<UInt32, KwsUser> UserTree = new SortedDictionary<UInt32, KwsUser>();

        /// <summary>
        /// Reference to the root virtual user.
        /// </summary>
        [NonSerialized, NonReplicated]
        public KwsUser RootUser;

        /// <summary>
        /// Reference to the workspace creator, if any.
        /// </summary>
        [NonReplicated]
        public KwsUser Creator
        {
            get
            {
                return GetNonVirtualUserByID(1);
            }
        }

        /// <summary>
        /// Reference to the local user. This user may be virtual.
        /// </summary>
        public KwsUser LocalUser
        {
            get
            {
                return GetUserByID(m_cd.Credentials.UserID);
            }
        }

        public void Relink(KwsCoreData cd)
        {
            m_cd = cd;
            RootUser = new KwsUser();
            RootUser.AdminFlag = true;
            RootUser.ManagerFlag = true;
            RootUser.RegisterFlag = true;
            RootUser.VirtualFlag = true;
            RootUser.AdminName = RootUser.UserName = "System Administrator";
        }

        /// <summary>
        /// Return the user having the ID specified, if any. Virtual users
        /// may be returned.
        /// </summary>
        public KwsUser GetUserByID(UInt32 ID)
        {
            if (ID == 0) return RootUser;
            if (UserTree.ContainsKey(ID)) return UserTree[ID];

            KwsCredentials creds = m_cd.Credentials;
            if (ID == creds.UserID)
            {
                KwsUser user = new KwsUser();
                user.UserID = creds.UserID;
                user.AdminName = creds.UserName;
                user.UserName = creds.UserName;
                user.EmailAddress = creds.UserEmailAddress;
                user.VirtualFlag = true;
                return user;
            }

            return null;
        }

        /// <summary>
        /// Return the non-virtual user having the ID specified, if any.
        /// </summary>
        public KwsUser GetNonVirtualUserByID(UInt32 ID)
        {
            if (UserTree.ContainsKey(ID)) return UserTree[ID];
            return null;
        }

        /// <summary>
        /// Return the non-virtual user having the email address specified,
        /// if any.
        /// </summary>
        public KwsUser GetUserByEmailAddress(String emailAddress)
        {
            foreach (KwsUser u in UserTree.Values) if (emailAddress.ToLower() == u.EmailAddress.ToLower()) return u;
            return null;
        }
    }

    /// <summary>
    /// Data of the KFS application in a workspace.
    /// </summary>
    [KwmSerializable]
    public class KfsAppData
    {
        /// <summary>
        /// Revision ID of the permanent state. This value is incremented when 
        /// the state to serialize changes.
        /// </summary>
        [NonSerialized, NonReplicated]
        public UInt64 PermanentRevID = 0;

        /// <summary>
        /// Revision ID of the permanent state last serialized.
        /// </summary>
        [NonSerialized, NonReplicated]
        public UInt64 SerializationRevID = 0;
    }

    /// <summary>
    /// This class contains the core data of the workspace (credentials, user
    /// list, etc).
    /// </summary>
    [KwmSerializable]
    public class KwsCoreData
    {
        /// <summary>
        /// Workspace credentials.
        /// </summary>
        public KwsCredentials Credentials = new KwsCredentials();

        /// <summary>
        /// Users of the workspace.
        /// </summary>
        public KwsUserInfo UserInfo = new KwsUserInfo();

        /// <summary>
        /// KCD state of the workspace.
        /// </summary>
        [NonReplicated]
        public KwsKcdState KcdState = new KwsKcdState();

        /// <summary>
        /// Data of the KFS application. This object is not serialized in the
        /// same object graph as KwsCoreData to save time.
        /// </summary>
        [NonSerialized]
        public KfsAppData KfsAd = new KfsAppData();

        /// <summary>
        /// 'Health' status of the workspace (Good, OnTheWayOut, etc).
        /// </summary>
        public KwsMainStatus MainStatus = KwsMainStatus.NotYetSpawned;

        /// <summary>
        /// Rebuild flags.
        /// </summary>
        public KwsRebuildFlag RebuildFlags = 0;

        /// <summary>
        /// Task currently in progress. This field must be Stop when the
        /// workspace manager is stopped. Only the workspace state machine
        /// is allowed to modify this field.
        /// </summary>
        [NonSerialized]
        public KwsTask CurrentTask = KwsTask.Stop;

        /// <summary>
        /// Task that the user wants the workspace to be executing. This can
        /// be WorkOnline, WorkOffline or Stop. The state machine will switch
        /// to that task when possible. Only the workspace state machine is
        /// allowed to modify this field.
        /// </summary>
        public KwsTask UserTask = KwsTask.Stop;

        /// <summary>
        /// Last error that occurred. This field is cleared whenever the KWM
        /// attempts to recover from the error. Only the workspace state 
        /// machine is allowed to modify this field.
        /// </summary>
        public Exception LastException = null;

        /// <summary>
        /// Status of the applications of the workspace.
        /// </summary>
        public KwsAppStatus AppStatus = KwsAppStatus.Stopped;

        /// <summary>
        /// UUID of the workspace. This changes at every rebuild.
        /// </summary>
        public byte[] Uuid = Wm.MakeUuid();

        /// <summary>
        /// ID of the next EAnp event.
        /// </summary>
        [NonReplicated]
        public UInt64 NextEAnpID = 1;

        /// <summary>
        /// Revision ID of the permanent state. This value is incremented when
        /// the state to serialize changes.
        /// </summary>
        [NonSerialized, NonReplicated]
        public UInt64 PermanentRevID = 0;

        /// <summary>
        /// Revision ID of the permanent state last serialized.
        /// </summary>
        [NonSerialized, NonReplicated]
        public UInt64 SerializationRevID = 0;

        /// <summary>
        /// Revision ID of the transient state. This value is incremented
        /// when the state to push to the clients changes.
        /// </summary>
        [NonSerialized, NonReplicated]
        public UInt64 TransientRevID = 0;

        /// <summary>
        /// Creation date of the workspace if known, otherwise UINT64.MaxValue.
        /// </summary>
        [NonReplicated]
        public UInt64 CreationDate
        {
            get
            {
                if (UserInfo.Creator != null) return UserInfo.Creator.InvitationDate;
                return UInt64.MaxValue;
            }
        }

        public void Relink()
        {
            UserInfo.Relink(this);
        }
    }
}