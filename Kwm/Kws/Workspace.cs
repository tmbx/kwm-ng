using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace kwm
{
    /// <summary>
    /// This class represents a workspace on a KCD.
    /// </summary>
    public class Workspace
    {
        /// <summary>
        /// Array of application IDs supported by the workspace.
        /// </summary>
        public static UInt32[] AppIdList = { KAnp.KANP_NS_CHAT,
                                             KAnp.KANP_NS_KFS, 
                                             KAnp.KANP_NS_VNC,
                                             KAnp.KANP_NS_PB };
        
        /// <summary>
        /// Unique identifier assigned to the workspace by the WM. It is
        /// different from the identifier assigned to the workspace by the KCD.
        /// </summary> 
        public UInt64 InternalID = 0;

        /// <summary>
        /// Core workspace data. All permanent data must be stored in this
        /// object. The serializable data must form a tree.
        /// </summary>
        public KwsCoreData Cd = null;

        /// <summary>
        /// Reference to the KCD associated to this workspace.
        /// </summary>
        public WmKcd Kcd = null;

        /// <summary>
        /// Reference to the workspace state machine.
        /// </summary>
        public KwsStateMachine Sm = new KwsStateMachine();

        /// <summary>
        /// Handle the workspace events received from the KCD that do not
        /// concern the applications.
        /// </summary>
        public KwsKcdEventHandler KcdEventHandler = new KwsKcdEventHandler();

        /// <summary>
        /// Workspace KCD login handler. 
        /// </summary>
        public KwsKcdLoginHandler KcdLoginHandler = new KwsKcdLoginHandler();

        /// <summary>
        /// Reference to the chat application.
        /// </summary>
        public AppChat Chat = new AppChat();

        /// <summary>
        /// Reference to the public workspace application.
        /// </summary>
        public AppPb Pb = new AppPb();

        /// <summary>
        /// Reference to the VNC application.
        /// </summary>
        public AppVnc Vnc = new AppVnc();

        /// <summary>
        /// Reference to the KFS application.
        /// </summary>
        public AppKfs Kfs = new AppKfs();

        /// <summary>
        /// Tree of applications indexed by application ID.
        /// </summary>
        public SortedDictionary<UInt32, KwsApp> AppTree = new SortedDictionary<UInt32, KwsApp>();

        /// <summary>
        /// Workspace listeners.
        /// </summary>
        public EventHandler<KwsSmNotif> OnKwsSmNotif;

        public void Relink(UInt64 internalID, KwsCoreData cd)
        {
            InternalID = internalID;
            Cd = cd;
            Cd.Relink();
            Wm.KwsTree[InternalID] = this;
            Kcd = Wm.GetOrCreateKcd(Cd.Credentials.KcdID);
            Kcd.KwsTree[InternalID] = this;
            Sm.Relink(this);
            KcdEventHandler.Relink(this);
            KcdLoginHandler.Relink(this);
            Chat.Relink(this);
            Pb.Relink(this);
            Vnc.Relink(this);
            Kfs.Relink(this);
        }

        /// <summary>
        /// Return the application having the ID specified, if any.
        /// </summary>
        public KwsApp GetApp(UInt32 id)
        {
            if (AppTree.ContainsKey(id)) return AppTree[id];
            return null;
        }


        ///////////////////////////////////////////
        // Interface methods for state machines. //
        ///////////////////////////////////////////

        /// <summary>
        /// Store the event specified in the database with the status specified.
        /// </summary>
        public void StoreKAnpEventInDb(AnpMsg msg, KwsAnpEventStatus status)
        {
            Wm.LocalDbBroker.StoreKAnpEvent(InternalID, msg, status);
        }

        /// <summary>
        /// Get the first unprocessed event from the database, if any.
        /// </summary>
        public AnpMsg GetFirstUnprocessedEventInDb()
        {
            return Wm.LocalDbBroker.GetFirstUnprocessedKAnpEvent(InternalID);
        }

        /// <summary>
        /// Get the last KAnp event stored in the database, if any.
        /// </summary>
        public AnpMsg GetLastKAnpEventInDb()
        {
            return Wm.LocalDbBroker.GetLastKAnpEvent(InternalID);
        }

        /// <summary>
        /// Update the status of the KAnp event specified in the database.
        /// </summary>
        public void UpdateKAnpEventStatusInDb(UInt64 msgID, KwsAnpEventStatus status)
        {
            Wm.LocalDbBroker.UpdateKAnpEventStatus(InternalID, msgID, status);
        }

        /// <summary>
        /// Delete all the events associated to the workspace from the database.
        /// </summary>
        public void DeleteEventsFromDb()
        {
            Wm.LocalDbBroker.RemoveKAnpEvents(InternalID);
            Wm.LocalDbBroker.RemoveEAnpEvents(InternalID);
        }

        /// <summary>
        /// Return true if the workspace is in the KCD connect tree.
        /// </summary>
        public bool InKcdConnectTree()
        {
            return Kcd.KwsConnectTree.ContainsKey(InternalID);
        }

        /// <summary>
        /// Add this workspace to the KCD connect tree if it is not already
        /// there
        /// </summary>
        public void AddToKcdConnectTree()
        {
            if (!InKcdConnectTree()) Kcd.KwsConnectTree[InternalID] = this;
        }

        /// <summary>
        /// Remove this workspace from the KCD connect tree if it is there.
        /// </summary>
        public void RemoveFromKcdConnectTree()
        {
            if (InKcdConnectTree()) Kcd.KwsConnectTree.Remove(InternalID);
        }

        /// <summary>
        /// Return true if the workspace is in the workspace removal tree.
        /// </summary>
        public bool InKwsRemoveTree()
        {
            return Wm.KwsRemoveTree.ContainsKey(InternalID);
        }

        /// <summary>
        /// Add this workspace to the workspace removal tree if it is not
        /// already there.
        /// </summary>
        public void AddToKwsRemoveTree()
        {
            if (!InKwsRemoveTree()) Wm.KwsRemoveTree[InternalID] = this;
        }

        /// <summary>
        /// Remove this workspace from the workspace removal tree if it is
        /// there.
        /// </summary>
        public void RemoveFromKwsRemoveTree()
        {
            if (InKwsRemoveTree()) Wm.KwsRemoveTree.Remove(InternalID);
        }

        /// <summary>
        /// Prepare the workspace to work online or offline. This method
        /// throws on error.
        /// </summary>
        public void PrepareToWork()
        {
            // Create the workspace state directory.
            Directory.CreateDirectory(KwsRoamingStatePath);
        }

        /// <summary>
        /// Prepare the workspace to be rebuilt. This method throws on error.
        /// </summary>
        public void PrepareToRebuild()
        {
            // Delete the events and update the KCD state.
            if ((Cd.RebuildFlags & KwsRebuildFlag.FlushKcdData) > 0)
            {
                DeleteEventsFromDb();
                Cd.KcdState.LastReceivedEventId = 0;
                Cd.KcdState.NbUnprocessedEvent = 0;
            }

            // Clear the user tree.
            Cd.UserInfo.UserTree.Clear();
        }

        /// <summary>
        /// Prepare the workspace to be removed. This method must not throw.
        /// </summary>
        public void PrepareToRemove()
        {
            // Remove the database data.
            DeleteEventsFromDb();
            Wm.LocalDbBroker.DeleteSerializedObject("kws_" + InternalID + "_core");
            Wm.LocalDbBroker.DeleteSerializedObject("kws_" + InternalID + "_kfs");
            Wm.LocalDbBroker.RemoveKwsFromKwsList(InternalID);

            // Delete the workspace state directory.
            try { Directory.Delete(KwsRoamingStatePath, true); }
            catch (Exception) { }
        }


        /////////////////////////////////////////////
        // Interface methods for external parties. //
        /////////////////////////////////////////////

        /// <summary>
        /// Path to the directory containing the workspace state.
        /// </summary>
        public String KwsRoamingStatePath
        {
            get { return KwmPath.GetKwmRoamingStatePath() + "workspaces\\" + InternalID + "\\"; }
        }

        /// <summary>
        /// Return true if the workspace should be displayed in the workspace browser.
        /// </summary>
        public bool IsDisplayable()
        {
            return (Cd.MainStatus == KwsMainStatus.Good || Cd.MainStatus == KwsMainStatus.RebuildRequired);
        }

        public ulong GetExternalKwsID()
        {
            return Cd.Credentials.ExternalID;
        }

        public bool IsPublicKws()
        {
            return Cd.Credentials.PublicFlag;
        }

        public string GetKwsName()
        {
            return Cd.Credentials.KwsName;
        }

        public string GetKwsUniqueName()
        {
            return GetKwsName() + " (" + InternalID + ")";
        }

        /// <summary>
        /// Return the current run level of the workspace (Stopped, Offline, Online).
        /// </summary>
        public KwsRunLevel GetRunLevel() { return Sm.GetRunLevel(); }

        /// <summary>
        /// Return true if the workspace has a level of functionality greater
        /// or equal to the offline mode.
        /// </summary>
        public bool IsOfflineCapable() { return GetRunLevel() >= KwsRunLevel.Offline; }

        /// <summary>
        /// Return true if the workspace has a level of functionality equal to
        /// the online mode.
        /// </summary>
        public bool IsOnlineCapable() { return GetRunLevel() == KwsRunLevel.Online; }

        /// <summary>
        /// Create a new KCD ANP command message having the minor version and 
        /// type specified and a unique ID. The workspace ID is inserted as the
        /// first element of the command.
        /// </summary>
        public AnpMsg NewKcdCmd(UInt32 type)
        {
            AnpMsg msg = Wm.NewKcdCmd(Kcd.MinorVersion, type);
            msg.AddUInt64(Cd.Credentials.ExternalID);
            return msg;
        }

        /// <summary>
        /// Post a KCD command.
        /// </summary>
        public KcdQuery PostKcdCmd(AnpMsg cmd, KcdQueryDelegate callback)
        {
            KcdQuery query = new KcdQuery(cmd, callback, Kcd, this);
            Wm.PostKcdQuery(query);
            return query;
        }

        /// <summary>
        /// Create a ktlstunnel for this workspace.
        /// </summary>
        public AnpTunnel CreateTunnel()
        {
            return new AnpTunnel(Kcd.KcdID.Host, Kcd.KcdID.Port);
        }

        /// <summary>
        /// Update and return the current freshness time. The freshness time 
        /// returned is set to the "stale" value if the KCD is not up to date.
        /// </summary>
        public UInt64 GetKcdFreshnessTime()
        {
            if (!Cd.KcdState.KcdEventUpToDateFlag) return WmCoreData.StaleTime;
            return Wm.Cd.UpdateFreshnessTime();
        }

        /// <summary>
        /// Create an EAnp event having the parameters specified.
        /// </summary>
        private AnpMsg MakeEAnpEvent(EAnpEvt type, UInt64 date, UInt32 userID, UInt64 freshness)
        {
            AnpMsg m = new AnpMsg();
            m.Minor = 1;
            m.Type = (UInt32)type;
            m.AddUInt64(InternalID);
            m.AddUInt64(date);
            m.AddUInt32(userID);
            m.AddUInt64(freshness);
            m.AddBin(Wm.MakeUuid());
            return m;
        }

        /// <summary>
        /// Create a permanent EAnp event having the type, date and user
        /// specified. The freshness time is set to the current KCD freshness 
        /// time.
        /// </summary>
        public AnpMsg MakePermEAnpEvent(EAnpEvt type, UInt64 date, UInt32 userID)
        {
            return MakeEAnpEvent(type, date, userID, GetKcdFreshnessTime());
        }

        /// <summary>
        /// Create a transient EAnp event having the type specified. The 
        /// freshness time is set to the WM freshness time.
        /// </summary>
        public AnpMsg MakeTransientEAnpEvent(EAnpEvt type)
        {
            return MakeEAnpEvent(type, 
                                 KDate.DateTimeToKDate(DateTime.Now),
                                 Cd.UserInfo.LocalUser.UserID,
                                 Wm.Cd.UpdateFreshnessTime());
        }

        /// <summary>
        /// Post a permanent EAnp event. An ID is assigned to the event.
        /// </summary>
        public void PostPermEAnpEvent(AnpMsg evt)
        {
            evt.ID = Cd.NextEAnpID++;
            Wm.LocalDbBroker.StoreEAnpEvent(InternalID, evt);
            WmEAnp.SendTransientEvent(evt);
        }

        /// <summary>
        /// Post a transient EAnp event.
        /// </summary>
        public void PostTransientEAnpEvent(AnpMsg evt)
        {
            WmEAnp.SendTransientEvent(evt);
        }

        /// <summary>
        /// This method should be called when the state of the workspace 
        /// changes.
        /// </summary>
        public void OnStateChange(WmStateChange c)
        {
            if (c == WmStateChange.Internal || c == WmStateChange.Permanent)
                Cd.PermanentRevID++;

            if (c == WmStateChange.Permanent || c == WmStateChange.Transient)
            {
                Cd.TransientRevID++;
                WmEAnp.OnWmStateChange();
            }
        }
    }
}