using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml;

namespace kwm
{
    /// <summary>
    /// Represent an EAnp incoming query linked to a WM core operation.
    /// </summary>
    public class WmEAnpQueryCoreOp
    {
        /// <summary>
        /// Reference to the EAnp incoming query.
        /// </summary>
        private EAnpIncomingQuery m_incomingQuery = null;

        /// <summary>
        /// Reference to the WM core operation.
        /// </summary>
        private WmCoreOp m_coreOp = null;

        /// <summary>
        /// Result to send when the core operation completes.
        /// </summary>
        private AnpMsg m_res = null;

        public WmEAnpQueryCoreOp(EAnpIncomingQuery incomingQuery, WmCoreOp coreOp, AnpMsg res)
        {
            m_incomingQuery = incomingQuery;
            m_coreOp = coreOp;
            m_res = res;
        }

        /// <summary>
        /// Handle the query.
        /// </summary>
        public void Start()
        {
            LinkCallback();
            m_coreOp.Start();
        }

        /// <summary>
        /// Link the incoming query and core operation callback handlers.
        /// </summary>
        private void LinkCallback()
        {
            m_incomingQuery.OnCancellation += OnIncomingQueryCancellation;
            m_coreOp.OnCompletion += OnCoreOpCompletion;
        }

        /// <summary>
        /// Unlink the incoming query and core operation callback handlers.
        /// </summary>
        private void UnlinkCallback()
        {
            m_incomingQuery.OnCancellation -= OnIncomingQueryCancellation;
            m_coreOp.OnCompletion -= OnCoreOpCompletion;
        }

        /// <summary>
        /// Called when the incoming query gets cancelled.
        /// </summary>
        private void OnIncomingQueryCancellation(Object sender, EventArgs args)
        {
            UnlinkCallback();
            m_coreOp.Cancel();
        }

        /// <summary>
        /// Called when the core operation completes.
        /// </summary>
        private void OnCoreOpCompletion()
        {
            UnlinkCallback();
            Reply();
        }

        /// <summary>
        /// Reply to the query. This is a no-op if the incoming query is no
        /// longer pending.
        /// </summary>
        private void Reply()
        {
            try
            {
                m_coreOp.FormatReply(m_res);
                m_incomingQuery.Reply(m_res);
            }

            catch (Exception ex)
            {
                KBase.HandleException(ex, true);
            }
        }
    }

    /// <summary>
    /// Represent an EAnpChannel opened by the WM EAnp broker.
    /// </summary>
    public class WmEAnpChannel
    {
        /// <summary>
        /// Reference to the channel.
        /// </summary>
        public EAnpChannel Channel;

        /// <summary>
        /// True if the client needs to resynchronize its view of the state of
        /// the WM.
        /// </summary>
        public bool NeedSyncFlag = true;

        public WmEAnpChannel(EAnpChannel c)
        {
            Channel = c;
            c.OnIncomingQuery += HandleIncomingQuery;
        }

        /// <summary>
        /// Called when an incoming query is received.
        /// </summary>
        public void HandleIncomingQuery(Object sender, EAnpIncomingQueryEventArgs args)
        {
            // Get the query.
            EAnpIncomingQuery query = args.Query;
            if (!query.IsPending()) return;

            // Create the result message.
            AnpMsg res = new AnpMsg();
            res.Type = (uint)EAnpRes.OK;
            
            // Dispatch.
            WmCoreOp coreOp = null;

            try
            {
                AnpMsg cmd = query.Cmd;
                EAnpCmd t = (EAnpCmd)cmd.Type;

                // Commands with core operations.
                if (t == EAnpCmd.RegisterKps) coreOp = MakeCoreOpFromCmd(new WmCoreOpRegisterKps(), cmd);
                else if (t == EAnpCmd.SetKwsTask) coreOp = MakeCoreOpFromCmd(new KwsCoreOpSetKwsTask(), cmd);
                else if (t == EAnpCmd.SetLoginPwd) coreOp = MakeCoreOpFromCmd(new KwsCoreOpSetLoginPwd(), cmd);
                else if (t == EAnpCmd.CreateKws) coreOp = MakeCoreOpFromCmd(new KwsCoreOpCreateKws(), cmd);
                else if (t == EAnpCmd.InviteKws) coreOp = MakeCoreOpFromCmd(new KwsCoreOpInviteKws(), cmd);
                else if (t == EAnpCmd.LookupRecAddr) coreOp = MakeCoreOpFromCmd(new WmCoreOpLookupRecAddr(), cmd);
                else if (t == EAnpCmd.ChatPostMsg) coreOp = MakeCoreOpFromCmd(new KwsCoreOpChatPostMsg(), cmd);
                else if (t == EAnpCmd.PbAcceptChat) coreOp = MakeCoreOpFromCmd(new KwsCoreOpPbAcceptChat(), cmd);

                // Commands without core operations.
                else if (t == EAnpCmd.ExportKws) HandleExportKws(cmd, res);
                else if (t == EAnpCmd.ImportKws) HandleImportKws(cmd, res);
                else if (t == EAnpCmd.VncCreateSession) HandleVncCreateSession(cmd, res);
                else if (t == EAnpCmd.VncJoinSession) HandleVncJoinSession(cmd, res);
                else if (t == EAnpCmd.CheckEventUuid) HandleCheckEventUuid(cmd, res);
                else if (t == EAnpCmd.FetchEvent) HandleFetchEvent(cmd, res);
                else if (t == EAnpCmd.FetchState) HandleFetchState(cmd, res);

                // Eeep!
                else
                {
                    res.Type = (UInt32)EAnpRes.Failure;
                    (new EAnpExGeneric("invalid EAnp command type")).Serialize(res);
                }
            }

            catch (Exception ex)
            {
                res.Type = (UInt32)EAnpRes.Failure;
                res.ClearPayload();
                EAnpException castedEx = EAnpException.FromException(ex);
                castedEx.Serialize(res);
            }

            if (!query.IsPending()) return;

            // We got a core operation. Start it.
            if (coreOp != null)
            {
                try
                {
                    WmEAnpQueryCoreOp qco = new WmEAnpQueryCoreOp(query, coreOp, res);
                    qco.Start();
                }

                catch (Exception ex)
                {
                    KBase.HandleException(ex, true);
                }
            }

            // Reply to the query right away.
            else
            {
                query.Reply(res);
            }
        }

        /// <summary>
        /// Have the core operation specified parse the command specified. The
        /// core operation is returned for convenience.
        /// </summary>
        private WmCoreOp MakeCoreOpFromCmd(WmCoreOp op, AnpMsg cmd)
        {
            op.Parse(cmd);
            return op;
        }


        ///////////////////////
        // Command handlers. //
        ///////////////////////

        /// <summary>
        /// Export workspaces.
        /// </summary>
        private void HandleExportKws(AnpMsg cmd, AnpMsg res)
        {
            int i = 0;
            UInt64 kwsID = cmd.Elements[i++].UInt64;
            String destPath = cmd.Elements[i++].String;
            WmKwsImportExport.ExportKws(destPath, kwsID);
        }

        /// <summary>
        /// Import workspaces.
        /// </summary>
        private void HandleImportKws(AnpMsg cmd, AnpMsg res)
        {
            int i = 0;
            String credString = cmd.Elements[i++].String;
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(credString);
            WmKwsImportExport.ImportKwsListFromXmlDoc(doc);
        }

        /// <summary>
        /// Create a VNC session.
        /// </summary>
        private void HandleVncCreateSession(AnpMsg cmd, AnpMsg res)
        {
            int i = 0;
            UInt64 kwsID = cmd.Elements[i++].UInt64;
            bool supportFlag = cmd.Elements[i++].UInt32 > 0;
            String subject = cmd.Elements[i++].String;
            Workspace kws = Wm.GetKwsByInternalIDOrThrow(kwsID);
            byte[] sessionUuid = kws.Vnc.StartServerSession(supportFlag, 0, subject);
            res.Type = (uint)EAnpRes.VncSession;
            res.AddBin(sessionUuid);
        }

        /// <summary>
        /// Join a VNC session.
        /// </summary>
        private void HandleVncJoinSession(AnpMsg cmd, AnpMsg res)
        {
            int i = 0;
            UInt64 kwsID = cmd.Elements[i++].UInt64;
            UInt64 sessionID = cmd.Elements[i++].UInt64;
            String subject = cmd.Elements[i++].String;
            Workspace kws = Wm.GetKwsByInternalIDOrThrow(kwsID);
            byte[] sessionUuid = kws.Vnc.StartClientSession(sessionID, subject);
            res.Type = (uint)EAnpRes.VncSession;
            res.AddBin(sessionUuid);
        }

        /// <summary>
        /// Verify the EAnp event UUID specified.
        /// </summary>
        private void HandleCheckEventUuid(AnpMsg cmd, AnpMsg res)
        {
            int i = 0;
            UInt64 kwsID = cmd.Elements[i++].UInt64;
            UInt64 eventID = cmd.Elements[i++].UInt64;
            byte[] uuid = cmd.Elements[i++].Bin;
            AnpMsg evt = Wm.LocalDbBroker.GetEAnpEvent(kwsID, eventID);
            if (evt == null) throw new EAnpExGeneric("no such event");
            if (!KUtil.ByteArrayEqual(evt.Elements[4].Bin, uuid)) throw new EAnpExGeneric("uuid mismatch");
        }

        /// <summary>
        /// Fetch EAnp events.
        /// </summary>
        private void HandleFetchEvent(AnpMsg cmd, AnpMsg res)
        {
            int i = 0;
            UInt64 kwsID = cmd.Elements[i++].UInt64;
            UInt32 evtID = cmd.Elements[i++].UInt32;
            UInt32 limit = cmd.Elements[i++].UInt32;
            List<AnpMsg> l = Wm.LocalDbBroker.FetchEAnpEvents(kwsID, evtID, limit);
            res.Type = (uint)EAnpRes.FetchEvent;
            res.AddUInt64(Wm.Cd.UpdateFreshnessTime());
            res.AddUInt32((uint)l.Count);
            foreach (AnpMsg evt in l) res.AddBin(evt.ToByteArray(true));
        }
        
        /// <summary>
        /// Fetch an update of the state of the KWM.
        /// </summary>
        private void HandleFetchState(AnpMsg cmd, AnpMsg res)
        {
            // The client is now synchronized.
            NeedSyncFlag = false;
        }
    }

    /// <summary>
    /// Handle the EAnp interactions in the WM.
    /// </summary>
    public static class WmEAnp
    {
        /// <summary>
        /// Tree of WmEAnp channels indexed by EAnp channel.
        /// </summary>
        private static SortedDictionary<EAnpChannel, WmEAnpChannel> m_channelTree = 
            new SortedDictionary<EAnpChannel, WmEAnpChannel>();

        /// <summary>
        /// Create a transient event message.
        /// </summary>
        public static AnpMsg MakeEvent()
        {
            return new AnpMsg();
        }

        /// <summary>
        /// Send a transient event to all open channels.
        /// </summary>
        public static void SendTransientEvent(AnpMsg evt)
        {
            foreach (WmEAnpChannel wc in GetChannelArray()) wc.Channel.SendEvt(evt);
        }

        /// <summary>
        /// Notify the clients when the state of the WM has changed.
        /// </summary>
        public static void OnWmStateChange()
        {
            foreach (WmEAnpChannel wc in GetChannelArray())
            {
                if (wc.NeedSyncFlag) continue;
                wc.NeedSyncFlag = true;
                AnpMsg evt = MakeEvent();
                evt.Type = (uint)EAnpEvt.FetchState;
                wc.Channel.SendEvt(evt);
            }
        }

        /// <summary>
        /// Called when a channel has been opened.
        /// </summary>
        public static void HandleChannelOpen(Object sender, EAnpChannelOpenEventArgs args)
        {
            EAnpChannel c = args.Channel;
            if (!c.IsOpen()) return;

            // Register the channel.
            c.OnClose += HandleChannelClose;
            WmEAnpChannel wc = new WmEAnpChannel(c);
            m_channelTree[c] = wc;
        }

        /// <summary>
        /// Called when a channel has been closed.
        /// </summary>
        private static void HandleChannelClose(Object sender, EventArgs args)
        {
            EAnpChannel c = (EAnpChannel)sender;
            if (!m_channelTree.ContainsKey(c)) return;
            m_channelTree.Remove(c);
        }

        /// <summary>
        /// Return an array of open channels.
        /// </summary>
        private static WmEAnpChannel[] GetChannelArray()
        {
            WmEAnpChannel[] a = new WmEAnpChannel[m_channelTree.Values.Count];
            m_channelTree.Values.CopyTo(a, 0);
            return a;
        }
    }
}