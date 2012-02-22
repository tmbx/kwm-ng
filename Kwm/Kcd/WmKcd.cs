using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace kwm
{
    /// <summary>
    /// Method called when the KCD query results are ready.
    /// </summary>
    public delegate void KcdQueryDelegate(KcdQuery query);

    /// <summary>
    /// Represent a query made on a KCD.
    /// </summary>
    public class KcdQuery
    {
        /// <summary>
        /// ANP command of this query.
        /// </summary>
        public AnpMsg Cmd = null;

        /// <summary>
        /// ANP reply of this query.
        /// </summary>
        public AnpMsg Res = null;

        /// <summary>
        /// KCD associated to the query.
        /// </summary>
        public WmKcd Kcd;

        /// <summary>
        /// Workspace associated to the query, if any.
        /// </summary>
        public Workspace Kws;

        /// <summary>
        /// Message ID associated to this query.
        /// </summary>
        public UInt64 MsgID { get { return Cmd.ID; } }

        /// <summary>
        /// Callback called when the query has completed.
        /// </summary>
        public KcdQueryDelegate Callback = null;

        public KcdQuery(AnpMsg cmd, KcdQueryDelegate callback, WmKcd kcd, Workspace kws)
        {
            Debug.Assert(cmd.ID != 0);
            Cmd = cmd;
            Callback = callback;
            Kcd = kcd;
            Kws = kws;
        }

        /// <summary>
        /// Cancel/terminate the execution of this query if required.
        /// </summary>
        public void Terminate()
        {
            if (Kcd.QueryMap.ContainsKey(MsgID)) Kcd.QueryMap.Remove(MsgID);
        }
    }

    /// <summary>
    /// This class represents a KCD used by the workspace manager.
    /// </summary>
    public class WmKcd
    {
        /// <summary>
        /// Number of seconds that must elapse before trying to reconnect a KCD
        /// that disconnected due to an error.
        /// </summary>
        private const UInt32 ReconnectDelay = 60;

        /// <summary>
        /// ReconnectDelay scaling factor in the exponential backoff algorithm.
        /// </summary>
        private const UInt32 BackoffFactor = 4;

        /// <summary>
        /// Backoff limit to the exponential backoff algorithm.
        /// </summary>
        private const UInt32 MaxNbBackoff = 5;

        /// <summary>
        /// Identifier of the KCD.
        /// </summary>
        public KcdIdentifier KcdID;

        /// <summary>
        /// Tree of workspaces using this KCD indexed by internal
        /// workspace ID.
        /// </summary>
        public SortedDictionary<UInt64, Workspace> KwsTree = new SortedDictionary<UInt64, Workspace>();

        /// <summary>
        /// Tree of workspaces that want to be connected indexed
        /// by internal workspace ID.
        /// </summary>
        public SortedDictionary<UInt64, Workspace> KwsConnectTree = new SortedDictionary<UInt64, Workspace>();

        /// <summary>
        /// Tree mapping message IDs to KCD queries.
        /// </summary>
        public SortedDictionary<UInt64, KcdQuery> QueryMap = new SortedDictionary<UInt64, KcdQuery>();

        /// <summary>
        /// Connection status.
        /// </summary>
        public KcdConnStatus ConnStatus = KcdConnStatus.Disconnected;

        /// <summary>
        /// Date at which the last error occurred. The value MinValue
        /// indicates that no error is currently associated to the KCD.
        /// </summary>
        public DateTime ErrorDate = DateTime.MinValue;

        /// <summary>
        /// Number of consecutive connection attempts that failed. This is
        /// used for exponential backoff.
        /// </summary>
        public UInt32 FailedConnectCount = 0;

        /// <summary>
        /// Minor version of the protocol spoken with the KCD.
        /// </summary>
        public UInt32 MinorVersion = 0;

        public WmKcd(KcdIdentifier kcdID)
        {
            KcdID = kcdID;
        }

        /// <summary>
        /// Return the workspace having the external ID specified, if any.
        /// </summary>
        public Workspace GetWorkspaceByExternalID(UInt64 externalKwsId)
        {
            // This code could eventually be optimized if there are too many
            // workspaces.
            foreach (Workspace kws in KwsTree.Values)
                if (kws.Cd.Credentials.ExternalID == externalKwsId) return kws;
            return null;
        }

        /// <summary>
        /// Cancel the KCD queries related to the workspace specified.
        /// </summary>
        public void CancelKwsKcdQuery(Workspace kws)
        {
            List<KcdQuery> list = new List<KcdQuery>();
            foreach (KcdQuery query in QueryMap.Values)
                if (query.Kws == kws) list.Add(query);
            foreach (KcdQuery query in list) query.Terminate();
        }

        /// <summary>
        /// Clear the current error status of the KCD, if any. If 
        /// clearFailedConnectFlag is true, the failed connection count is
        /// also cleared.
        /// </summary>
        public void ClearError(bool clearFailedConnectFlag)
        {
            ErrorDate = DateTime.MinValue;
            if (clearFailedConnectFlag) FailedConnectCount = 0;
        }

        /// <summary>
        /// Set the current error status and increase the number of failed
        /// connection attempts if requested.
        /// </summary>
        public void SetError(DateTime date, bool connectFailureFlag)
        {
            ErrorDate = date;
            if (connectFailureFlag) FailedConnectCount++;
        }

        /// <summary>
        /// Return the reconnection deadline, which is based on the current 
        /// error state and the failed connection count.
        /// </summary>
        public DateTime GetReconnectDeadline()
        {
            if (ErrorDate == DateTime.MinValue) return DateTime.MinValue;

            // If a troublesome KCD constantly fails after accepting the
            // connection, the failed connection count will remain 0. In that
            // case, we consider the failed connection count to be 1. By design
            // the number of backoffs is one less than the failed connection
            // count.
            UInt32 nbBackoff = FailedConnectCount;
            if (nbBackoff > 0) nbBackoff--;
            nbBackoff = Math.Min(nbBackoff, MaxNbBackoff);
            return ErrorDate.AddSeconds(ReconnectDelay * Math.Pow(BackoffFactor, nbBackoff));
        }
    }
}