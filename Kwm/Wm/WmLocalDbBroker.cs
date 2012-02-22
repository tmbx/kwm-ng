using kcslib;
using kwmlib;
using System;
using System.Data.Common;
using System.Diagnostics;
using System.Collections.Generic;

namespace kwm
{
    /// <summary>
    /// This class contains methods used to manipulate the local database.
    /// </summary>
    public class WmLocalDbBroker
    {
        /// <summary>
        /// Latest version number of the serialized data in the database.
        /// </summary>
        public const UInt32 LatestDbVersion = 3;

        /// <summary>
        /// Reference to the local database.
        /// </summary>
        private WmLocalDb m_db;

        public void Relink(WmLocalDb db)
        {
            m_db = db;
        }

        /// <summary>
        /// Create or upgrade the database. Throw an exception if the database
        /// version is newer than the version we can handle.
        /// </summary>
        public void InitDb()
        {
            UInt32 version = GetDbVersion();

            if (version == 0)
                CreateSchema();

            else if (version < LatestDbVersion)
                UpgradeDb(version);

            else if (version != LatestDbVersion)
                throw new Exception("this version of the database (" + version + ") cannot be handled by the KWM");
        }

        /// <summary>
        /// Return the version of the serialized data stored in the database.
        /// 0 is returned if the database does not contain serialized data.
        /// </summary>
        public UInt32 GetDbVersion()
        {
            String s = "SELECT name FROM sqlite_master WHERE type='table' AND name='db_version'";
            Object res = m_db.GetCmd(s).ExecuteScalar();
            if (res == null) return 0;
            res = m_db.GetCmd("SELECT version FROM db_version").ExecuteScalar();
            if (res == null) return 0;
            return (UInt32)(Int32)res; // The double cast is required.
        }

        /// <summary>
        /// Update the version number stored in the database.
        /// </summary>
        private void UpdateDbVersion(UInt32 version)
        {
            m_db.ExecNQ("UPDATE db_version SET version = " + version);
        }

        /// <summary>
        /// Create the initial database schema.
        /// </summary>
        private void CreateSchema()
        {
            KLogging.Log("Creating database schema.");

            m_db.BeginTransaction();

            String s =
                "CREATE TABLE 'db_version' ('version' INT PRIMARY KEY); " +
                "INSERT INTO db_version (version) VALUES (" + LatestDbVersion + "); " +
                "CREATE TABLE 'serialization' ('name' VARCHAR PRIMARY KEY, 'data' BLOB); " +
                "CREATE TABLE 'kws_list' ('kws_id' INT PRIMARY KEY, 'name' VARCHAR); " +
                "CREATE TABLE 'kanp_events' ('kws_id' INT, 'evt_id' INT, 'evt_data' BLOB, 'status' INT); " +
                "CREATE TABLE 'eanp_events' ('kws_id' INT, 'evt_id' INT, 'uuid' BLOB, 'evt_data' BLOB); " +
                "CREATE UNIQUE INDEX 'kanp_events_index_1' ON 'kanp_events' ('kws_id', 'evt_id'); " +
                "CREATE UNIQUE INDEX 'kanp_events_index_2' ON 'kanp_events' ('kws_id', 'status', 'evt_id'); " +
                "CREATE UNIQUE INDEX 'eanp_events_index_1' ON 'eanp_events' ('kws_id', 'evt_id'); ";
            m_db.ExecNQ(s);

            m_db.CommitTransaction();
        }

        /// <summary>
        /// Upgrade the database schema if required.
        /// </summary>
        private void UpgradeDb(UInt32 version)
        {
            KLogging.Log("Upgrading database schema from version " + version);

            m_db.BeginTransaction();

            UpdateDbVersion(LatestDbVersion);

            m_db.CommitTransaction();
        }

        /// <summary>
        /// Return a tree of workspace names indexed by workspace ID.
        /// </summary>
        public SortedDictionary<UInt64, String> GetKwsList()
        {
            try
            {
                DbCommand cmd = m_db.GetCmd("SELECT kws_id, name FROM kws_list");
                DbDataReader reader = cmd.ExecuteReader();
                SortedDictionary<UInt64, String> d = new SortedDictionary<UInt64, String>();
                while (reader.Read()) d[(UInt64)reader.GetInt64(0)] = reader.GetString(1);
                reader.Close();
                return d;
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
                return null;
            }
        }

        /// <summary>
        /// Remove the specified workspace from the list of workspaces.
        /// </summary>
        public void RemoveKwsFromKwsList(UInt64 id)
        {
            try
            {
                m_db.ExecNQ("DELETE FROM kws_list WHERE kws_id = " + id);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Add the workspace specified to the list of workspaces. 
        /// </summary>
        public void AddKwsToKwsList(UInt64 id, String name)
        {
            try
            {
                DbCommand cmd = m_db.GetCmd("INSERT INTO kws_list (kws_id, name) VALUES (?, ?);");
                m_db.AddParamToCmd(cmd, id);
                m_db.AddParamToCmd(cmd, name);
                cmd.ExecuteNonQuery();
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Remove the KAnp events associated to the workspace specified.
        /// </summary>
        public void RemoveKAnpEvents(UInt64 kwsID)
        {
            try
            {
                m_db.ExecNQ("DELETE FROM kanp_events WHERE kws_id = " + kwsID);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Remove the EAnp events associated to the workspace specified.
        /// </summary>
        public void RemoveEAnpEvents(UInt64 kwsID)
        {
            try
            {
                m_db.ExecNQ("DELETE FROM eanp_events WHERE kws_id = " + kwsID);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Store the KAnp event specified with the status specified.
        /// </summary>
        public void StoreKAnpEvent(UInt64 kwsID, AnpMsg msg, KwsAnpEventStatus status)
        {
            try
            {
                DbCommand cmd = m_db.GetCmd("INSERT INTO kanp_events (kws_id, evt_id, evt_data, status) VALUES (?, ?, ?, ?);");
                m_db.AddParamToCmd(cmd, kwsID);
                m_db.AddParamToCmd(cmd, msg.ID);
                m_db.AddParamToCmd(cmd, msg.ToByteArray(true));
                m_db.AddParamToCmd(cmd, status);
                cmd.ExecuteNonQuery();
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Store the EAnp event specified.
        /// </summary>
        public void StoreEAnpEvent(UInt64 kwsID, AnpMsg msg)
        {
            try
            {
                DbCommand cmd = m_db.GetCmd("INSERT INTO eanp_events (kws_id, evt_id, evt_data) VALUES (?, ?, ?);");
                m_db.AddParamToCmd(cmd, kwsID);
                m_db.AddParamToCmd(cmd, msg.ID);
                m_db.AddParamToCmd(cmd, msg.ToByteArray(true));
                cmd.ExecuteNonQuery();
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Update the status of the KAnp event specified.
        /// </summary>
        public void UpdateKAnpEventStatus(UInt64 kwsID, UInt64 msgID, KwsAnpEventStatus status)
        {
            try
            {
                String s = "UPDATE kanp_events SET status = " + (UInt32)status +
                           " WHERE kws_id = " + kwsID + " AND evt_id = " + msgID;
                m_db.ExecNQ(s);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Helper method for queries fetching an event.
        /// </summary>
        private AnpMsg GetEventFromQuery(String s)
        {
            Object res = m_db.GetCmd(s).ExecuteScalar();
            if (res == null) return null;
            AnpMsg m = new AnpMsg();
            m.FromByteArray((byte[])res, true);
            return m;
        }

        /// <summary>
        /// Get the first unprocessed KAnp event of the workspace specified, if
        /// any.
        /// </summary>
        public AnpMsg GetFirstUnprocessedKAnpEvent(UInt64 kwsID)
        {
            try
            {
                String s = "SELECT evt_data FROM kanp_events WHERE kws_id = " + kwsID +
                           " AND status = " + (UInt32)KwsAnpEventStatus.Unprocessed + " ORDER BY evt_id LIMIT 1";
                return GetEventFromQuery(s);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
                return null;
            }
        }

        /// <summary>
        /// Get the last KAnp event of the workspace specified, if any.
        /// </summary>
        public AnpMsg GetLastKAnpEvent(UInt64 kwsID)
        {
            try
            {
                String s = "SELECT evt_data FROM kanp_events WHERE kws_id = " + kwsID +
                           " AND evt_id = (SELECT max(evt_id) FROM kanp_events WHERE kws_id = " + kwsID + ");";
                return GetEventFromQuery(s);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
                return null;
            }
        }

        /// <summary>
        /// Get the EAnp event specified, if any.
        /// </summary>
        public AnpMsg GetEAnpEvent(UInt64 kwsID, UInt64 evtID)
        {
            try
            {
                String s = "SELECT evt_data FROM eanp_events WHERE kws_id = " + kwsID +
                           " AND evt_id = " + evtID + ";";
                return GetEventFromQuery(s);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
                return null;
            }
        }

        /// <summary>
        /// Fetch at most 'limit' EAnp events from the database, starting at 
        /// evtID.
        /// </summary>
        public List<AnpMsg> FetchEAnpEvents(UInt64 kwsID, UInt64 evtID, UInt32 limit)
        {
            String s = "SELECT evt_data FROM eanp_events WHERE kws_id = " + kwsID +
                       " AND evt_id > " + evtID + " ORDER BY evt_id LIMIT " + limit + ";";
            List<AnpMsg> res = new List<AnpMsg>();
            DbDataReader reader = m_db.GetCmd(s).ExecuteReader();
            while (reader.Read())
            {
                AnpMsg m = new AnpMsg();
                m.FromByteArray((byte[])reader.GetValue(0), true);
                res.Add(m);
            }
            return res;
        }

        /// <summary>
        /// Add/replace the specified object in the serialization table.
        /// </summary>
        public void AddSerializedObject(String name, byte[] data)
        {
            try
            {
                m_db.ExecNQ("DELETE FROM serialization WHERE name = '" + name + "'");
                DbCommand cmd = m_db.GetCmd("INSERT INTO serialization (name, data) VALUES(?, ?)");
                m_db.AddParamToCmd(cmd, name);
                m_db.AddParamToCmd(cmd, data);
                cmd.ExecuteNonQuery();
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Delete specified object in the serialization table.
        /// </summary>
        public void DeleteSerializedObject(String name)
        {
            try
            {
                m_db.ExecNQ("DELETE FROM serialization WHERE name = '" + name + "'");
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }

        /// <summary>
        /// Return the serialized object having the name specified, if any.
        /// </summary>
        public byte[] GetSerializedObject(String name)
        {
            try
            {
                DbCommand cmd = m_db.GetCmd("SELECT data FROM serialization WHERE name = '" + name + "'");
                return (byte[])cmd.ExecuteScalar();
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
                return null;
            }
        }

        /// <summary>
        /// Return true if the serialized object specified exists.
        /// </summary>
        public bool HasSerializedObject(String name)
        {
            try
            {
                return (m_db.GetCmd("SELECT name FROM serialization WHERE name = '" + name + "'").ExecuteScalar() != null);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
                return false;
            }
        }
    }
}