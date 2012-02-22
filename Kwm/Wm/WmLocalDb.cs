using kcslib;
using System;
using System.Data.Common;
using System.IO;
using System.Diagnostics;

namespace kwm
{
    /// <summary>
    /// This class represents the local SQLite database used to store the
    /// workspace manager data.
    /// </summary>
    public class WmLocalDb
    {
        /// <summary>
        /// Path to the database.
        /// </summary>
        private String m_dbPath = null;

        /// <summary>
        /// Internal connection.
        /// </summary>
        private DbConnection m_dbConn = null;

        /// <summary>
        /// Transaction currently in progress, if any.
        /// </summary>
        private DbTransaction m_transaction = null;

        /// <summary>
        /// Path to the SQLite database file.
        /// </summary>
        public String DbPath { get { return m_dbPath; } }

        /// <summary>
        /// Reference to the SQLite database connection, if any.
        /// </summary>
        public DbConnection DbConn { get { return m_dbConn; } }

        /// <summary>
        /// This method opens or creates the SQLite database file.
        /// </summary>
        public void OpenOrCreateDb(String dbPath)
        {
            // Get the factory used to create SQLite databases.
            DbProviderFactory factory = DbProviderFactories.GetFactory("System.Data.SQLite");

            // Set the path to the database.
            DbConnection conn = factory.CreateConnection();
            conn.ConnectionString = "Data Source=" + dbPath;

            // Make sure the path to the DB exists.
            String dirPart = Path.GetDirectoryName(dbPath);
            Directory.CreateDirectory(dirPart);

            // Open the database.
            conn.Open();

            // Set the reference to the path and the connection.
            m_dbPath = dbPath;
            m_dbConn = conn;
        }

        /// <summary>
        /// This method closes the database file if it is open. An exception
        /// is thrown on error.
        /// </summary>
        public void CloseDb()
        {
            if (m_dbConn != null)
            {
                try
                {
                    m_dbConn.Close();
                }

                finally
                {
                    m_dbConn = null;
                    m_transaction = null;
                }
            }
        }

        /// <summary>
        /// Delete the database if it exists. An exception is thrown on error.
        /// </summary>
        public void DeleteDb()
        {
            CloseDb();
            if (File.Exists(m_dbPath)) File.Delete(m_dbPath);
        }

        /// <summary>
        /// Return true if a transaction is open.
        /// </summary>
        public bool HasTransaction()
        {
            return (m_transaction != null);
        }

        /// <summary>
        /// Begin a database transaction. It is an error to open a transaction
        /// while another one is already open.
        /// </summary>
        public void BeginTransaction()
        {
            Debug.Assert(!HasTransaction());
            m_transaction = DbConn.BeginTransaction();
        }

        /// <summary>
        /// Commit the current database transaction.
        /// </summary>
        public void CommitTransaction()
        {
            Debug.Assert(HasTransaction());
            m_transaction.Commit();
            m_transaction = null;
        }

        /// <summary>
        /// Rollback the current database transaction, if any.
        /// </summary>
        public void RollbackTransaction()
        {
            if (!HasTransaction()) return;
            m_transaction.Rollback();
            m_transaction = null;
        }

        /// <summary>
        /// Return true if the database is open.
        /// </summary>
        public bool IsOpen()
        {
            return (m_dbConn != null);
        }

        /// <summary>
        /// Return a command object bound to this connection.
        /// </summary>
        public DbCommand GetCmd()
        {
            return DbConn.CreateCommand();
        }

        /// <summary>
        /// Return a command object bound to this connection having the command
        /// text specified.
        /// </summary>
        public DbCommand GetCmd(String text)
        {
            DbCommand cmd = GetCmd();
            cmd.CommandText = text;
            return cmd;
        }

        /// <summary>
        /// Execute the non-query specified.
        /// </summary>
        public void ExecNQ(String text)
        {
            GetCmd(text).ExecuteNonQuery();
        }

        /// <summary>
        /// Add a parameter having the value specified to the command specified.
        /// </summary>
        public void AddParamToCmd(DbCommand cmd, Object value)
        {
            DbParameter param = cmd.CreateParameter();
            param.Value = value;
            cmd.Parameters.Add(param);
        }
    }
}