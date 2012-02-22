using kcslib;
using kwmlib;
using System;

namespace kwm
{
    /// <summary>
    /// Backend class for the chat application.
    /// </summary>
    public class AppChat : KwsApp
    {
        public override UInt32 AppID { get { return KAnp.KANP_NS_CHAT; } }

        public override KwsAnpEventStatus HandleAnpEvent(AnpMsg evt)
        {
            // Incoming chat message.
            if (evt.Type == KAnp.KANP_EVT_CHAT_MSG)
            {
                UInt64 date = evt.Elements[1].UInt64;
                UInt32 userID = evt.Elements[3].UInt32;
                String userMsg = evt.Elements[4].String;
                
                AnpMsg etEvt = Kws.MakePermEAnpEvent(EAnpEvt.ChatMsgReceived, date, userID);
                etEvt.AddString(userMsg);
                Kws.PostPermEAnpEvent(etEvt);

                return KwsAnpEventStatus.Processed;
            }

            return KwsAnpEventStatus.Unprocessed;
        }
    }
}