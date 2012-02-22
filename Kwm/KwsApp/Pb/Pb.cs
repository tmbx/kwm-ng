using kcslib;
using kwmlib;
using System;

namespace kwm
{
    /// <summary>
    /// Backend class for the public workspace application.
    /// </summary>
    public class AppPb : KwsApp
    {
        public override UInt32 AppID { get { return KAnp.KANP_NS_PB; } }

        public override KwsAnpEventStatus HandleAnpEvent(AnpMsg evt)
        {
            if (evt.Type == KAnp.KANP_EVT_PB_TRIGGER_CHAT) return HandleTriggerChatEvent(evt);
            else if (evt.Type == KAnp.KANP_EVT_PB_TRIGGER_KWS) return HandleTriggerKwsEvent(evt);
            // We don't care about this event.
            else if (evt.Type == KAnp.KANP_EVT_PB_CHAT_ACCEPTED) return KwsAnpEventStatus.Processed;
            else return KwsAnpEventStatus.Unprocessed;
        }

        private KwsAnpEventStatus HandleTriggerChatEvent(AnpMsg evt)
        {
            UInt64 date = evt.Elements[1].UInt64;
            UInt64 reqID = evt.Minor <= 2 ? evt.Elements[2].UInt32 : evt.Elements[2].UInt64;
            UInt32 userID = evt.Elements[3].UInt32;
            String subject = evt.Elements[4].String;

            AnpMsg etEvt = Kws.MakePermEAnpEvent(EAnpEvt.PbChatRequested, date, userID);
            etEvt.AddUInt64(reqID);
            etEvt.AddString(subject);
            Kws.PostPermEAnpEvent(etEvt);

            return KwsAnpEventStatus.Processed;
        }

        private KwsAnpEventStatus HandleTriggerKwsEvent(AnpMsg evt)
        {
            UInt64 date = evt.Elements[1].UInt64;
            UInt32 userID = evt.Elements[3].UInt32;
            String subject = evt.Elements[4].String;

            AnpMsg etEvt = Kws.MakePermEAnpEvent(EAnpEvt.PbKwsRequested, date, userID);
            etEvt.AddString(subject);
            Kws.PostPermEAnpEvent(etEvt);

            return KwsAnpEventStatus.Processed;
        }
    }
}