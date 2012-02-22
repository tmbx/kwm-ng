using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace kwm
{
    /// <summary>
    /// Type of a state change in the WM.
    /// </summary>
    public enum WmStateChange
    {
        /// <summary>
        /// The state change is permanent but not externally visible.
        /// </summary>
        Internal,

        /// <summary>
        /// The state change is permanent and externally visible.
        /// </summary>
        Permanent,

        /// <summary>
        /// The state change is not permanent but it is externally visible.
        /// </summary>
        Transient
    }

    /// <summary>
    /// Represent a workspace folder.
    /// </summary>
    [KwmSerializable]
    public class WmKwsFolder
    {
        /// <summary>
        /// Full path to the folder.
        /// </summary>
        public String FullPath;

        /// <summary>
        /// Binary blob used by ET.
        /// </summary>
        public byte[] EtBlob;
    }

    /// <summary>
    /// This class contains the core data of the workspace manager.
    /// </summary>
    [KwmSerializable]
    public class WmCoreData
    {
        /// <summary>
        /// Value representing the absolute "stale" freshness time.
        /// </summary>
        public const UInt64 StaleTime = Int64.MaxValue;

        /// <summary>
        /// List of workspace folders.
        /// </summary>
        public List<WmKwsFolder> KwsFolderList = new List<WmKwsFolder>();

        /// <summary>
        /// Internal ID of the public workspace, if any. This is 0 if no public
        /// workspace exists.
        /// </summary>
        [NonReplicated]
        public UInt64 PublicKwsID = 0;

        /// <summary>
        /// Internal ID that should be given to the next workspace.
        /// </summary>
        [NonReplicated]
        public UInt64 NextKwsInternalID = 1;

        /// <summary>
        /// UUID of the WM.
        /// </summary>
        public byte[] Uuid = Wm.MakeUuid();

        /// <summary>
        /// Date at which the freshness time has last been updated.
        /// </summary>
        [NonSerialized, NonReplicated]
        public DateTime FreshnessDate = DateTime.Now;

        /// <summary>
        /// This field is used to assign a "freshness" timestamp to various
        /// events that is largely independent of the unreliable system clock.
        /// This field logically represents the running time of the KWM in 
        /// milliseconds. It is updated by computing the elapsed time since
        /// the last time it was refreshed, using the system clock, and by
        /// bounding that offset within reasonable values to cater for abrupt
        /// date changes.
        /// </summary>
        [NonReplicated]
        public UInt64 FreshnessTime = 0;

        /// <summary>
        /// Revision ID of the permanent state. This value is incremented when 
        /// the state to serialize changes.
        /// </summary>
        [NonSerialized, NonReplicated]
        public UInt64 PermanentRevID = 0;

        /// <summary>
        /// Revision ID of the permanent state last serialized.
        /// </summary>
        [NonSerialized, NonReplicated]
        public UInt64 SerializationRevID = 0;

        /// <summary>
        /// Revision ID of the transient state. This value is incremented
        /// when the state to push to the clients changes.
        /// </summary>
        [NonSerialized, NonReplicated]
        public UInt64 TransientRevID = 0;

        /// <summary>
        /// Last location of the VNC overlay.
        /// </summary>
        [NonReplicated]
        public Point VncOverlayLoc = new Point(0, 0);

        /// <summary>
        /// Update and return the freshness time.
        /// </summary>
        public UInt64 UpdateFreshnessTime()
        {
            DateTime now = DateTime.Now;
            Int64 offset = (Int64)(now - FreshnessDate).TotalMilliseconds;
            Int64 maxOffset = 24 * 60 * 60 * 1000;
            if (offset < 0) offset = 0;
            else if (offset > maxOffset) offset = maxOffset;
            FreshnessDate = now;
            FreshnessTime += (UInt64)offset;
            Wm.OnStateChange(WmStateChange.Internal);
            return FreshnessTime;
        }
    }
}