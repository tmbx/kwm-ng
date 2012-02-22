using kcslib;
using kwmlib;
using System;

namespace kwm
{
    /// <summary>
    /// Backend class for the KFS application.
    /// </summary>
    public class AppKfs : KwsApp
    {
        public override UInt32 AppID { get { return KAnp.KANP_NS_KFS; } }

        public override KwsAnpEventStatus HandleAnpEvent(AnpMsg evt)
        {
            return KwsAnpEventStatus.Processed;
        }
    }
}