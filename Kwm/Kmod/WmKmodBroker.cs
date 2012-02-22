using kcslib;
using kwmlib;
using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace kwm
{
    /// <summary>
    /// Status of a KMOD transaction.
    /// </summary>
    public enum KmodTransactionStatus
    {
        /// <summary>
        /// The transaction has not been queued yet.
        /// </summary>
        None,

        /// <summary>
        /// The transaction has been queued for execution.
        /// </summary>
        Queued,

        /// <summary>
        /// The transaction is being executed by KMOD.
        /// </summary>
        Executing,

        /// <summary>
        /// An error is being reported for the transaction.
        /// </summary>
        Failing,

        /// <summary>
        /// The transaction is finished.
        /// </summary>
        Finished
    }

    /// <summary>
    /// Reason why the KMOD transaction handler is being executed.
    /// </summary>
    public enum KmodTransactionReason : uint
    {
        /// <summary>
        /// The broker is ready to execute the first command.
        /// </summary>
        Start,

        /// <summary>
        /// The current command results are ready.
        /// </summary>
        CommandResult,

        /// <summary>
        /// An error has occurred with the KMOD process or thread.
        /// </summary>
        Error
    }

    /// <summary>
    /// A KMOD transaction consists in the execution of one or several K3P 
    /// commands with KMOD. Since multiple transactions may need to be executed
    /// at the same time, the KMOD broker maintains a queue of transactions
    /// which need to be executed by the KMOD process.
    /// 
    /// The commands of a transaction are executed sequentially, i.e. the
    /// executions of the transactions are not interleaved. Each command may or
    /// may not have an associated result. Commands which do not have an
    /// associated result may be sent in a batch to KMOD. When a command has an
    /// associated result, the resulting K3P elements must be read before the
    /// next command may be sent to KMOD. When all the elements have been read,
    /// the next command may be sent or the transaction may end.
    /// 
    /// A KMOD transaction is executed in UI context. To avoid blocking the UI
    /// thread for long, a KMOD transaction is executed in short periods
    /// through the callback method Run().
    /// 
    /// A KMOD transaction may be cancelled at any time after it has been
    /// submitted to a KMOD broker. A transaction may not be resubmitted after
    /// it has completed because there are possible race conditions.
    /// </summary>
    public abstract class KmodTransaction
    {
        /// <summary>
        /// Transaction status.
        /// </summary>
        public KmodTransactionStatus Status = KmodTransactionStatus.None;

        /// <summary>
        /// Broker associated to this transaction. Set when submitted to 
        /// the broker.
        /// </summary>
        public WmKmodBroker Broker = null;

        /// <summary>
        /// Exception describing the error that occurred, if any. Do not throw
        /// this since it can be shared between multiple transactions. For example,
        /// if 2 transactions are queued and the first one fails, the second transaction
        /// will be aborted and both Ex fields will be set to the same exception.
        /// </summary>
        public Exception Ex = null;

        /// <summary>
        /// Submit the transaction to the KMOD broker specified. The Run()
        /// method will be called when the broker is ready to execute the
        /// transaction. The broker guarantees that the transaction will be
        /// executed in the context of the UI and outside the current
        /// execution context. In other words, the Run() method will not be
        /// called before Submit() has returned.
        /// </summary>
        public void Submit(WmKmodBroker broker)
        {
            broker.SubmitTransaction(this);
        }

        /// <summary>
        /// Cancel the execution of the transaction if required. If the 
        /// transaction was being executed, the KMOD thread will be 
        /// stopped.
        /// </summary>
        public void Cancel()
        {
            if (Status == KmodTransactionStatus.None ||
                Status == KmodTransactionStatus.Finished) return;
            Broker.CancelTransaction(this);
        }

        /// <summary>
        /// Mark the transaction as finished when the results of
        /// the transaction have been received. Do not call this
        /// method in any other occasion.
        /// </summary>
        protected void Finish()
        {
            if (Status == KmodTransactionStatus.Executing) Status = KmodTransactionStatus.Finished;
        }

        /// <summary>
        /// Send the next command to KMOD.
        /// </summary>
        public void PostCommand(K3pMsg msg, bool haveResultFlag)
        {
            Broker.PostTransactionCommand(this, msg, haveResultFlag);
        }

        /// <summary>
        /// Return a sluper for reading the resulting K3P elements.
        /// </summary>
        public K3pSlurper GetSlurper()
        {
            return new K3pSlurper(GetNextK3pElement);
        }

        /// <summary>
        /// Return the next K3P element associated to the current command
        /// results. This method blocks in UI context if the next element has
        /// not been received from the KMOD process yet. The wait time is 
        /// expected to be short since KMOD sends all its results in one call
        /// to send().
        /// </summary>
        public K3pElement GetNextK3pElement()
        {
            return Broker.NextTransactionK3pElement(this);
        }

        /// <summary>
        /// This method is called by the KMOD broker in UI context when the
        /// first command is ready to be sent, when the current command results
        /// are ready and also when an error occurs in KMOD. If no command is posted
        /// during the call to Run(), the KMOD broker will assume that the
        /// transaction is completed.
        /// 
        /// IMPORTANT NOTES:
        /// - The Run() method may not reenter the UI. 
        /// - All the exceptions raised due to a communication error with KMOD
        ///   must be allowed to escape to the caller. The KMOD broker must
        ///   trap these exceptions to update its status correctly.
        /// </summary>
        /// <param name="reason">Reason why this method is being called</param>
        public abstract void Run(KmodTransactionReason reason);
    }

    /// <summary>
    /// Method called when the KMOD query results are ready.
    /// </summary>
    public delegate void KmodQueryDelegate(KmodQuery query);

    /// <summary>
    /// Simple KMOD transaction that supports only one command having a result.
    /// </summary>
    public class KmodQuery : KmodTransaction
    {
        /// <summary>
        /// Array of K3P commands to send to KMOD. Only the last command is 
        /// assumed to have an associated result.
        /// </summary>
        public K3pCmd[] InCmdArray = null;

        /// <summary>
        /// Command in the command array having a result.
        /// </summary>
        public K3pCmd InCmdWithRes = null;

        /// <summary>
        /// Callback called when the query has completed.
        /// </summary>
        public KmodQueryDelegate Callback = null;

        /// <summary>
        /// Message that describes the error that occurred while executing the
        /// query or the nature of the result if no error occurred. This 
        /// message is always set to a meaningful value when the query has
        /// completed.
        /// </summary>
        public String OutDesc = "";

        /// <summary>
        /// Message representing the result of the query. This is null if no
        /// message was obtained.
        /// </summary>
        public K3pMsg OutMsg = null;

        /// <summary>
        /// Submit a query to the broker specified. The command array must
        /// contain at least one command, the last of which being the command
        /// having an associated result. A session will be created to handle
        /// the commands. The callback function specified will be called when
        /// the results are available.
        /// </summary>
        public void Submit(WmKmodBroker broker, K3pCmd[] inCmdArray, KmodQueryDelegate callback)
        {
            Debug.Assert(inCmdArray.Length > 0);
            InCmdArray = inCmdArray;
            InCmdWithRes = InCmdArray[InCmdArray.Length - 1];
            Callback = callback;
            base.Submit(broker);
        }

        public override void Run(KmodTransactionReason reason)
        {
            if (reason == KmodTransactionReason.Start)
            {
                PostCommand(new K3p.k3p_beg_session(), false);
                for (int i = 0; i < InCmdArray.Length - 1; i++) PostCommand(InCmdArray[i], false);
                PostCommand(InCmdWithRes, true);
            }

            else if (reason == KmodTransactionReason.CommandResult)
            {
                // Important: let Slurp() throw.
                GetSlurper().Slurp(InCmdWithRes, out OutDesc, out OutMsg);

                // Post the end session command.
                PostCommand(new K3p.k3p_end_session(), false);

                // Mark the query as finished to allow the callback to cancel
                // the query without killing KMOD.
                Finish();

                // Call the callback.
                CallCallback();
            }

            else
            {
                OutDesc = Ex.Message;
                CallCallback();
            }
        }

        /// <summary>
        /// Call the callback function.
        /// </summary>
        private void CallCallback()
        {
            try
            {
                Callback(this);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }
    }

    /// <summary>
    /// Message sent to the KMOD broker to call Run() in a clean context.
    /// </summary>
    public class KmodBrokerWakeUpMsg : KThreadMsg
    {
        /// <summary>
        /// Reference to the broker.
        /// </summary>
        public WmKmodBroker Broker;

        public KmodBrokerWakeUpMsg(WmKmodBroker broker)
        {
            Broker = broker;
        }

        public override void Run()
        {
            Broker.Run();
        }
    }

    /// <summary>
    /// The KMOD broker manages the execution of transactions with KMOD.
    /// </summary>
    public class WmKmodBroker
    {
        /// <summary>
        /// Fired when the Kmod thread has been collected.
        /// </summary>
        public event EventHandler<EventArgs> OnThreadCollected;

        /// <summary>
        /// True if transactions are accepted by this broker.
        /// </summary>
        private bool m_enabledFlag = true;

        /// <summary>
        /// Queue of pending KMOD transactions.
        /// </summary>
        private List<KmodTransaction> m_transactionQueue = new List<KmodTransaction>();

        /// <summary>
        /// Transaction currently being executed by this broker.
        /// </summary>
        private KmodTransaction m_curTransaction = null;

        /// <summary>
        /// Command currently being executed by this broker.
        /// </summary>
        private KmodThreadCommand m_curCommand = null;

        /// <summary>
        /// Thread currently managing the KMOD process. 
        /// </summary>
        private KmodThread m_curThread = null;

        /// <summary>
        /// Message posted to execute the state machine.
        /// </summary>
        private KmodBrokerWakeUpMsg m_wakeUpMsg = null;

        /// <summary>
        /// This method is called to stop the broker. It returns true when the
        /// broker is ready to stop.
        /// </summary>
        public bool TryStop()
        {
            SetEnabled(false);
            return (m_curThread == null);
        }

        /// <summary>
        /// Enable or disable the broker.
        /// </summary>
        public void SetEnabled(bool enabledFlag)
        {
            m_enabledFlag = enabledFlag;
            if (!m_enabledFlag) Killall(new Exception("KMOD broker disabled"));
            else RequestRun();
        }

        public void SubmitTransaction(KmodTransaction transaction)
        {
            Debug.Assert(transaction.Status == KmodTransactionStatus.None);
            Debug.Assert(!m_transactionQueue.Contains(transaction));
            Debug.Assert(m_curTransaction != transaction);
            transaction.Status = KmodTransactionStatus.Queued;
            transaction.Broker = this;
            transaction.Ex = null;
            m_transactionQueue.Add(transaction);
            RequestRun();
        }

        public void CancelTransaction(KmodTransaction transaction)
        {
            KmodTransactionStatus prevStatus = transaction.Status;
            transaction.Status = KmodTransactionStatus.Finished;
            transaction.Ex = new Exception("transaction cancelled");

            // Cancel a queued transaction.
            if (prevStatus == KmodTransactionStatus.Queued)
                m_transactionQueue.Remove(transaction);

            // Cancel an executing transaction. We have to stop KMOD since the
            // transaction is under way.
            else if (prevStatus == KmodTransactionStatus.Executing)
            {
                Debug.Assert(transaction == m_curTransaction);
                EndCurrentTransaction();
                StopKmodThread();
                RequestRun();
            }
        }

        public void PostTransactionCommand(KmodTransaction transaction, K3pMsg msg, bool haveResultFlag)
        {
            Debug.Assert(transaction == m_curTransaction);
            Debug.Assert(IsTransactionExecuting());
            Debug.Assert(IsThreadReadyForUserCommand());
            KmodThreadCommand cmd = new KmodThreadCommand(m_curThread, msg, haveResultFlag);
            m_curThread.PostToWorker(cmd);
            m_curCommand = haveResultFlag ? cmd : null;
        }

        public K3pElement NextTransactionK3pElement(KmodTransaction transaction)
        {
            Debug.Assert(transaction == m_curTransaction);
            Debug.Assert(IsTransactionExecuting());
            Debug.Assert(IsThreadReadyForUserCommand());
            Debug.Assert(m_curCommand != null);
            Debug.Assert(m_curCommand.ResultReadyFlag);
            return m_curThread.GetNextK3pElement();
        }

        /// <summary>
        /// Run the broker state machine. Only call RequestRun() to execute
        /// this method.
        /// </summary>
        public void Run()
        {
            Debug.Assert(m_wakeUpMsg != null);

            try
            {
                // Loop until our state stabilize.
                while (WantToRunNow())
                {
                    Debug.Assert(m_curTransaction == null);
                    Debug.Assert(m_curCommand == null);

                    // We're disabled. Kill all transactions.
                    if (!m_enabledFlag)
                        Killall(new Exception("KMOD broker disabled"));

                    // Execute the next transaction.
                    else
                    {
                        m_curTransaction = m_transactionQueue[0];
                        m_transactionQueue.RemoveAt(0);
                        Debug.Assert(m_curTransaction.Status == KmodTransactionStatus.Queued);
                        m_curTransaction.Status = KmodTransactionStatus.Executing;

                        // We have to start KMOD.
                        if (m_curThread == null) StartKmodThread();

                        // Execute the current transaction.
                        else StartCurrentTransaction();
                    }
                }
            }

            // We cannot recover from these errors.
            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }

            m_wakeUpMsg = null;
        }

        /// <summary>
        /// Handle the completion of the KMOD thread.
        /// </summary>
        public void OnThreadCompletion()
        {
            // Get the reference to the exception, if any.
            Exception ex = m_curThread.Ex;

            // Clear the reference to the thread.
            m_curThread = null;

            // Handle the error if it was yet unhandled.
            if (ex != null || m_curTransaction != null)
            {
                if (ex == null) ex = new Exception("unexpected thread termination");
                Killall(ex);
            }

            // Notify the listeners.
            if (OnThreadCollected != null) OnThreadCollected(this, null);

            // Process the next transactions.
            RequestRun();
        }

        /// <summary>
        /// Called when a KMOD thread notification is received.
        /// </summary>
        public void OnThreadNotification(KmodThreadCommand command)
        {
            // We're don't care about this notification anymore.
            if (command != m_curCommand) return;

            // The result of the command is ready.
            m_curCommand.ResultReadyFlag = true;

            // This is the notification for the connection.
            if (m_curCommand.Msg is K3p.k3p_connect) HandleConnectResult();

            // This is the notification for the user's command.
            else GetNextUserCommand(KmodTransactionReason.CommandResult);
        }

        /// <summary>
        /// Return true if the state machine wants to run now.
        /// </summary>
        private bool WantToRunNow()
        {
            return (m_curTransaction == null &&
                    (m_curThread == null || IsThreadReadyForUserCommand()) &&
                    m_transactionQueue.Count > 0);
        }

        /// <summary>
        /// Return true if there is an executing transaction.
        /// </summary>
        private bool IsTransactionExecuting()
        {
            return (m_curTransaction != null && m_curTransaction.Status == KmodTransactionStatus.Executing);
        }

        /// <summary>
        /// Return true if the KMOD thread is ready to execute a user command.
        /// </summary>
        private bool IsThreadReadyForUserCommand()
        {
            return (m_curThread != null && !m_curThread.CancelFlag && m_curThread.HaveConnectResultFlag);
        }

        /// <summary>
        /// Request the state machine to run, if required.
        /// </summary>
        private void RequestRun()
        {
            if (WantToRunNow() && m_wakeUpMsg == null)
            {
                m_wakeUpMsg = new KmodBrokerWakeUpMsg(this);
                KBase.ExecInUI(new KBase.EmptyDelegate(m_wakeUpMsg.Run));
            }
        }

        /// <summary>
        /// Start the KMOD thread and send it the connect command.
        /// </summary>
        private void StartKmodThread()
        {
            Debug.Assert(m_curThread == null);
            Debug.Assert(m_curCommand == null);
            m_curThread = new KmodThread(this);
            m_curThread.Start();
            m_curCommand = new KmodThreadCommand(m_curThread, new K3p.k3p_connect(), true);
            m_curThread.PostToWorker(m_curCommand);
        }

        /// <summary>
        /// Stop the KMOD thread if it is running. The current command is cleared.
        /// </summary>
        private void StopKmodThread()
        {
            if (m_curThread != null) m_curThread.RequestCancellation();
            m_curCommand = null;
        }

        /// <summary>
        /// Start the execution of the current transaction.
        /// </summary>
        private void StartCurrentTransaction()
        {
            Debug.Assert(IsTransactionExecuting());
            Debug.Assert(IsThreadReadyForUserCommand());
            GetNextUserCommand(KmodTransactionReason.Start);
        }

        /// <summary>
        /// Set the current transaction status to finished, clear the current 
        /// transaction and command.
        /// </summary>
        private void EndCurrentTransaction()
        {
            if (m_curTransaction != null)
            {
                m_curTransaction.Status = KmodTransactionStatus.Finished;
                m_curTransaction = null;
            }

            m_curCommand = null;
        }

        /// <summary>
        /// Stop the KMOD thread and kill all pending and executing
        /// transactions.
        /// </summary>
        private void Killall(Exception ex)
        {
            // Get the list of failing transactions and clear the current data
            // structures.
            List<KmodTransaction> list = new List<KmodTransaction>();
            list.AddRange(m_transactionQueue);
            m_transactionQueue.Clear();

            if (m_curTransaction != null)
            {
                list.Add(m_curTransaction);
                m_curTransaction = null;
            }

            // Mark the transactions as failing.
            foreach (KmodTransaction transaction in list) transaction.Status = KmodTransactionStatus.Failing;

            // Stop the thread if it is running.
            StopKmodThread();

            // Kill all transactions.
            foreach (KmodTransaction transaction in list)
            {
                if (transaction.Status != KmodTransactionStatus.Failing) continue;
                transaction.Status = KmodTransactionStatus.Finished;
                transaction.Ex = ex;
                if (ex != null)
                    KLogging.LogException(ex);

                try
                {
                    transaction.Run(KmodTransactionReason.Error);
                }

                catch (Exception ex2)
                {
                    KBase.HandleException(ex2, true);
                }
            }
        }

        /// <summary>
        /// Execute the current transaction with the reason specified to obtain
        /// the next user command.
        /// </summary>
        private void GetNextUserCommand(KmodTransactionReason reason)
        {
            Debug.Assert(IsTransactionExecuting());
            Debug.Assert(IsThreadReadyForUserCommand());

            // Remember the previous command posted by the user.
            KmodThreadCommand prevCmd = m_curCommand;

            try
            {
                // Call Run(). Note that the transaction may be cancelled
                // or finished during that time.
                m_curTransaction.Run(reason);

                // If the transaction is still executing and the user hasn't 
                // posted another command or if the transaction has explicitly
                // been marked finished, the transaction is completed.
                if ((IsTransactionExecuting() && (m_curCommand == null || m_curCommand == prevCmd)) ||
                    (m_curTransaction != null && m_curTransaction.Status == KmodTransactionStatus.Finished))
                {
                    EndCurrentTransaction();
                    RequestRun();
                }
            }

            // The transaction has failed. Kill everything and request a run.
            catch (Exception ex)
            {
                Killall(ex);
                RequestRun();
            }
        }

        /// <summary>
        /// Handle the KMOD connection result.
        /// </summary>
        private void HandleConnectResult()
        {
            try
            {
                // Get an element reader and read the tool info.
                K3pElementReader reader = new K3pElementReader(m_curThread.GetNextK3pElement);
                UInt32 ins = reader.Ins();
                if (ins != K3p.KMO_COGITO_ERGO_SUM)
                    throw new Exception("unexpected reply to KMOD connect command");
                (new K3p.kmo_tool_info()).FromElementReader(reader);

                // We have received the connect results.
                m_curThread.HaveConnectResultFlag = true;

                // We no longer have a command.
                m_curCommand = null;

                // If we have a transaction, start it.
                if (m_curTransaction != null) StartCurrentTransaction();
            }

            catch (Exception ex)
            {
                Killall(ex);
            }

            RequestRun();
        }
    }
}
