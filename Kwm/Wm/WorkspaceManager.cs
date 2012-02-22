using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Diagnostics;

/* The KWM is a relatively complex application that has to deal with some
 * non-trivial scalability and synchronization issues. In order to the keep the
 * complexity manageable, the following design has been used.
 * 
 * The WorkspaceManager class contains the top-level logic of the KWM. It
 * contains the workspace list and knows how to initialize and terminate the
 * application and how to deal with errors.
 * 
 * The Workspace class contains the information related to a particular
 * workspace.
 * 
 * The WorkspaceManager and the Workspace classes are tightly coupled. They know
 * the internals of each other and cooperate closely to get the work done.
 * 
 * Both the WorkspaceManager and the Workspace classes use a state machine to
 * manage their state. The state machines contain the logic that dictates the
 * actions to take when some event occurs, such as the failure of a KCD server.
 * The main benefits of using state machines is that the logic is regrouped in
 * one place, making it easier to analyze the behavior of the program.
 * 
 * The state machines invoke the methods of the applications to start/stop the
 * applications, notify them that their state has changed and to deliver events
 * to them.
 * 
 * The WorkspaceManager and the Workspace classes expose their state publically.
 * For instance, the WorkspaceManager contains a public tree of workspaces. This
 * allows the state machines to access and modify this data directly.
 * 
 * To keep things simple, there are two conventions that must be respected: -
 * Keep the core logic buried in the state machines and their helpers. - Don't
 * modify the core public data members of the WorkspaceManager and the Workspace
 * classes outside these two classes, their state machines and their helpers.
 * 
 * 
 * Notes about threads and reentrance:
 * 
 * Windows guarantees that only one thread is executing in the context of the UI
 * at any given moment. This means that there is no need for explicit
 * synchronization when a thread is executing in the context of the UI; it can
 * manipulate UI objects and access the state of most of the objects of the KWM.
 * 
 * The messages posted to the UI by the worker threads are delivered to a thread
 * executing in UI context when no other thread is executing in the UI context.
 * These messages are guaranteed to be received in the order they are posted.
 * 
 * One thing to keep in mind is that the UI context is reentrant. When a thread
 * executing in UI context calls something like MessageBox.Show(), it
 * relinquishes the control of the UI until the user causes the method to
 * return. In the mean time, the thread can re-enter the UI context to handle
 * other events like timer events.
 * 
 * Writing reentrant code is tricky and the state machines are NOT reentrant.
 * Therefore, it is critically important to never call things like
 * MessageBox.Show() from any method that is called from a state machine.
 * 
 * If a method needs to reenter the UI while it is executing in the context of a
 * state machine, it must post a message to the UI thread to do so. It is the
 * responsability of the message to revalidate the context that led to its
 * generation; the state of the KWM may have changed by the time the message is
 * executed.
 *
 *
 * Notes about the data management strategies.
 *
 * The data of the KWM can be broken down in three categories: global data,
 * workspace data and application data.
 * 
 * The global data includes the list of workspaces and anything that is not
 * workspace-specific. The workspace data includes the data of a workspace that
 * is not application-specific, such as the workspace credentials. The
 * application data includes information about a specific application in a
 * specific workspace, such as the KFS view.
 * 
 * The various data elements have different lifetimes. The transient elements
 * are discarded when the KWM close. The cached elements are discarded when the
 * KWM is upgraded. The permanent elements are kept permanently.
 * 
 * The various data elements are stored in RAM, in the local database, on the
 * local filesystem and in the Windows registry. We keep most of the global,
 * workspace and application data in RAM. The events received from the KCD are
 * stored in the database when they are received. Some KFS data is stored in the
 * database and on the filesystem.
 * 
 * There are several synchronization challenges. The ET must be notified when
 * the state changes. Furthermore, the KWM must save its state when it is
 * quitting and also on a regular basis to reduce data losses.
 * 
 * For performance reasons, we don't want to scan all the data to detect which
 * elements have changed. Instead, we associate a revision ID to the various
 * data elements. The revision ID is incremented when the state changes. The ET
 * broker and the KWM state serializer store those revision IDs in their data
 * structures and compare them with the current revision IDs to detect which
 * elements have changed since the last time they processed the state.
 * 
 * We have to consider several factors when saving the state in the database or
 * storing new data into the database.
 * 
 * Firstly, the state must be kept coherent at all times, i.e. it must be
 * possible to reload the state previously saved in the database if the KWM
 * closes unexpectedly.
 * 
 * We achieve this with long-running database transactions. A transaction is
 * opened when the KWM is started and it is closed after the state has been
 * saved cleanly in the database. Then, a new transaction is opened. This scheme
 * ensures that any modification to the database will not be made permanent
 * until the state in the database is resynchronized with the state in RAM.
 * 
 * Secondly, it must be fast to save and restore the state from the database. To
 * this end, we store the object graph of the workspace core data objects
 * and some application objects in the database using the our custom 
 * automated serialization facilities. This saves us the trouble of serializing 
 * and deserializing all the state data manually and this is reasonably fast.
 * 
 * The object graphs are manually updated when the software is upgraded. We
 * use intermediate data structures that do not depend on concrete classes to 
 * make that process painless. The upgrade code "dies" once it has been written;
 * we never want to modify it again.
 * 
 * The KWM initialization proceeds as follow:
 * - Deserialize or create the various object trees of the KWM. This step must 
 *   not fail or all the user's data is lost.
 * - Relink the objects within the object graphs. This may create cycles in the
 *   object graphs. This step must not fail or all the user's data is lost.
 * - Initialize the objects in the graphs. If this step fails, the KWM quits
 *   but the user's data is not lost.
 */

namespace kwm
{
    /// <summary>
    /// Main status of the workspace manager.
    /// </summary>
    public enum WmMainStatus
    {
        /// <summary>
        /// The workspace manager is stopped. The KWM can either exit or
        /// start the workspace manager.
        /// </summary>
        Stopped,

        /// <summary>
        /// The workspace manager will switch to the 'stopped' state when
        /// everything has been cleaned up properly.
        /// </summary>
        Stopping,

        /// <summary>
        /// The workspace manager is initialized and operating normally.
        /// </summary>
        Started,

        /// <summary>
        /// The workspace manager is initializing its state.
        /// </summary>
        Starting
    }

    /// <summary>
    /// This class implements the KWM application logic.
    /// </summary>
    public static class Wm
    {
        /// <summary>
        /// Core data of the workspace manager.
        /// </summary>
        public static WmCoreData Cd;

        /// <summary>
        /// Reference to the local SQLite database.
        /// </summary>
        public static WmLocalDb LocalDb = new WmLocalDb();

        /// <summary>
        /// Reference to the local SQLite database broker.
        /// </summary>
        public static WmLocalDbBroker LocalDbBroker = new WmLocalDbBroker();

        /// <summary>
        /// Reference to the KCD broker.
        /// </summary>
        public static WmKcdBroker KcdBroker = new WmKcdBroker();

        /// <summary>
        /// This broker manages the interaction with KMOD.
        /// </summary>
        public static WmKmodBroker KmodBroker = new WmKmodBroker();

        /// <summary>
        /// This broker manages the interactions with the ET.
        /// </summary>
        public static EAnpServerBroker EAnpBroker = new EAnpServerBroker();

        /// <summary>
        /// Tree of KCDs indexed by KCD identifier.
        /// </summary>
        public static SortedDictionary<KcdIdentifier, WmKcd> KcdTree = new SortedDictionary<KcdIdentifier, WmKcd>();

        /// <summary>
        /// Tree of workspaces indexed by internal ID.
        /// </summary>
        public static SortedDictionary<UInt64, Workspace> KwsTree = new SortedDictionary<UInt64, Workspace>();

        /// <summary>
        /// Tree of workspaces that are being removed indexed by internal ID.
        /// </summary>
        public static SortedDictionary<UInt64, Workspace> KwsRemoveTree = new SortedDictionary<UInt64, Workspace>();

        /// <summary>
        /// Main status of the workspace manager.
        /// </summary>
        public static WmMainStatus MainStatus = WmMainStatus.Stopped;

        /// <summary>
        /// Pseudo-random UUID generator.
        /// </summary>
        public static Random UuidGen = new Random();

        public static void Relink(WmDeserializer ds)
        {
            Cd = ds.WmCd;
            WmSm.Relink();
            KcdBroker.OnEvent += WmSm.HandleKcdBrokerNotification;
            KmodBroker.OnThreadCollected += WmSm.OnThreadCollected;
            EAnpBroker.OnClose += WmSm.OnThreadCollected;
            EAnpBroker.OnChannelOpen += WmEAnp.HandleChannelOpen;

            foreach (UInt64 internalID in ds.KwsCdList.Keys)
            {
                Workspace kws = new Workspace();
                KwsCoreData kwsCd = ds.KwsCdList[internalID];
                kws.Relink(internalID, kwsCd);
            }

            AdjustPublicKwsID();
        }

        /// <summary>
        /// Serialize the state of the WM and the workspaces that have changed
        /// since the last serialization time.
        /// </summary>
        public static void Serialize()
        {
            // We should have a transaction open.
            Debug.Assert(LocalDb.HasTransaction());

            // Serialize the dirty objects.
            if (Wm.Cd.SerializationRevID != Wm.Cd.PermanentRevID)
            {
                Wm.Cd.SerializationRevID = Wm.Cd.PermanentRevID;
                SerializeObject("wm_core", Wm.Cd);
            }

            foreach (Workspace kws in KwsTree.Values)
            {
                if (kws.Cd.SerializationRevID != kws.Cd.PermanentRevID)
                {
                    kws.Cd.SerializationRevID = kws.Cd.PermanentRevID;
                    SerializeObject("kws_" + kws.InternalID + "_core", kws.Cd);
                }

                if (kws.Cd.KfsAd.SerializationRevID != kws.Cd.KfsAd.PermanentRevID)
                {
                    kws.Cd.KfsAd.SerializationRevID = kws.Cd.KfsAd.PermanentRevID;
                    SerializeObject("kws_" + kws.InternalID + "_kfs", kws.Cd.KfsAd);
                }
            }

            // Commit the lingering transaction.
            LocalDb.CommitTransaction();

            // Open a new lingering transaction.
            LocalDb.BeginTransaction();
        }

        /// <summary>
        /// Serialize the object specified and store it under the name
        /// specified in the database.
        /// </summary>
        private static void SerializeObject(String name, Object obj)
        {
            KLogging.Log("Serializing " + name + ".");
        }

        /// <summary>
        /// Generate a new UUID.
        /// </summary>
        public static byte[] MakeUuid()
        {
            byte[] uuid = new byte[16];
            UuidGen.NextBytes(uuid);
            return uuid;
        }

        /// <summary>
        /// Create a new workspace having the credentials specified and insert
        /// it in the workspace manager, with the current task Stop.
        /// </summary>
        public static Workspace CreateWorkspace(KwsCredentials creds)
        {
            try
            {
                // Clear the public flag if we already have a public workspace.
                if (Cd.PublicKwsID != 0) creds.PublicFlag = false;

                // Get a new internal ID.
                UInt64 internalID = Cd.NextKwsInternalID++;

                // Register the worskpace in the workspace manager.
                KwsCoreData kwsCd = new KwsCoreData();
                kwsCd.Credentials = creds;
                Workspace kws = new Workspace();
                kws.Relink(internalID, kwsCd);
                AdjustPublicKwsID();

                // Insert the workspace in the workpace list in the database.
                LocalDbBroker.AddKwsToKwsList(kws.InternalID, kws.Cd.Credentials.KwsName);

                // The WM state has changed.
                Wm.OnStateChange(WmStateChange.Permanent);

                return kws;
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
                return null;
            }
        }

        /// <summary>
        /// Remove the workspace object from the workspace manager. Used by the
        /// WM state machine.
        /// </summary>
        public static void RemoveWorkspace(Workspace kws)
        {
            try
            {
                WmKcd kcd = kws.Kcd;

                // Make sure the state is valid.
                Debug.Assert(KwsTree.ContainsKey(kws.InternalID));
                if (kcd != null)
                {
                    Debug.Assert(kcd.KwsTree.ContainsKey(kws.InternalID));
                    Debug.Assert(!kcd.KwsConnectTree.ContainsKey(kws.InternalID));
                }

                // Unregister the workspace from the workspace manager.
                KwsTree.Remove(kws.InternalID);
                if (KwsRemoveTree.ContainsKey(kws.InternalID)) KwsRemoveTree.Remove(kws.InternalID);
                if (kcd != null)
                {
                    kcd.CancelKwsKcdQuery(kws);
                    kcd.KwsTree.Remove(kws.InternalID);
                    RemoveKcdIfNoRef(kcd);
                }
                AdjustPublicKwsID();

                // Delete the workspace data.
                kws.PrepareToRemove();
                foreach (KwsApp app in kws.AppTree.Values) app.PrepareToRemove();
                
                // The WM state has changed.
                Wm.OnStateChange(WmStateChange.Permanent);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Create the WmKcd object specified if it does not exist, and return 
        /// a reference to the WmKcd object specified, if any.
        /// </summary>
        public static WmKcd GetOrCreateKcd(KcdIdentifier kcdID)
        {
            if (kcdID.Host == "") return null;
            if (!KcdTree.ContainsKey(kcdID)) KcdTree[kcdID] = new WmKcd(kcdID);
            return KcdTree[kcdID];
        }

        /// <summary>
        /// Unregister the WmKcd object specified if there are no more references
        /// to it and it is disconnected.
        /// </summary>
        public static void RemoveKcdIfNoRef(WmKcd kcd)
        {
            if (kcd.KwsTree.Count == 0 && kcd.ConnStatus == KcdConnStatus.Disconnected)
                KcdTree.Remove(kcd.KcdID);
        }
        
        /// <summary>
        /// Return the public workspace if there is one.
        /// </summary>
        public static Workspace GetPublicKws()
        {
            if (Cd.PublicKwsID != 0) return KwsTree[Cd.PublicKwsID];
            return null;
        }

        /// <summary>
        /// Ensure that the value of PublicKwsID is consistent. This method
        /// should be called when the WM is deserialized and when a workspace is
        /// added / removed.
        /// </summary>
        private static void AdjustPublicKwsID()
        {
            UInt64 id = Cd.PublicKwsID;

            // If we have a public workspace ID, check whether the workspace exists
            // and is public.
            if (id != 0 && (!KwsTree.ContainsKey(id) || !KwsTree[id].IsPublicKws()))
                id = 0;

            // Find a public workspace if there is one.
            if (id == 0)
            {
                foreach (Workspace kws in KwsTree.Values)
                {
                    if (kws.IsPublicKws())
                    {
                        id = kws.InternalID;
                        break;
                    }
                }
            }

            Cd.PublicKwsID = id;
        }

        /// <summary>
        /// Return the workspace having the internal ID specified, if any.
        /// </summary>
        public static Workspace GetKwsByInternalID(UInt64 internalID)
        {
            if (KwsTree.ContainsKey(internalID)) return KwsTree[internalID];
            return null;
        }

        /// <summary>
        /// Get the workspace having the internal ID specified, or throw an exception.
        /// </summary>
        public static Workspace GetKwsByInternalIDOrThrow(UInt64 ID)
        {
            Workspace kws = GetKwsByInternalID(ID);
            if (kws == null) throw new Exception("no such " + KwmStrings.Kws);
            return kws;
        }

        /// <summary>
        /// Return the workspace having the Kcd ID and external ID specified, 
        /// if any.
        /// </summary>
        public static Workspace GetKwsByExternalID(KcdIdentifier kcdID, UInt64 externalID)
        {
            if (externalID == 0) return null;
            foreach (Workspace kws in KwsTree.Values)
                if (kws.Kcd.KcdID.CompareTo(kcdID) == 0 && kws.Cd.Credentials.ExternalID == externalID)
                    return kws;
            return null;
        }

        /// <summary>
        /// Return the workspace having the KWMO hostname and external ID
        /// specified, if any.
        /// </summary>
        public static Workspace GetKwsByKwmoHostname(String host, UInt64 externalID)
        {
            if (host == "" || externalID == 0) return null;
            foreach (Workspace kws in KwsTree.Values)
                if (kws.Cd.Credentials.KwmoAddress == host && kws.Cd.Credentials.ExternalID == externalID)
                    return kws;
            return null;
        }

        /// <summary>
        /// Create a new KCD ANP command message having the minor version and 
        /// type specified and a unique ID.
        /// </summary>
        public static AnpMsg NewKcdCmd(UInt32 minorVersion, UInt32 type)
        {
            AnpMsg msg = new AnpMsg();
            msg.Major = KAnp.Major;
            msg.Minor = minorVersion;
            msg.Type = type;
            msg.ID = WmKcdState.NextKcdCmdID++;
            return msg;
        }

        /// <summary>
        /// Post the KCD query specified.
        /// </summary>
        public static void PostKcdQuery(KcdQuery query)
        {
            Debug.Assert(!query.Kcd.QueryMap.ContainsKey(query.MsgID));
            query.Kcd.QueryMap[query.MsgID] = query;
            KcdBroker.SendAnpMsgToKcd(new KcdAnpMsg(query.Cmd, query.Kcd.KcdID));
        }

        /// <summary>
        /// This method should be called when the state of the WM changes.
        /// </summary>
        public static void OnStateChange(WmStateChange c)
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
