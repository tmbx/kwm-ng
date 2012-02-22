using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Xml;
using System.Diagnostics;
using System.IO;

namespace kwm
{
    public class KwmSerializable : Attribute
    {
    }

    public class NonReplicated : Attribute
    {
    }

    /// <summary>
    /// This object is responsible for deserializing the state of the WM.
    /// </summary>
    public class WmDeserializer
    {
        /// <summary>
        /// Core data of the workspace manager.
        /// </summary>
        public WmCoreData WmCd = null;

        /// <summary>
        /// Tree of workspace core data, indexed by internal ID.
        /// </summary>
        public SortedDictionary<UInt64, KwsCoreData> KwsCdList = null;

        /// <summary>
        /// Exception raised on error.
        /// </summary>
        public Exception Ex;

        // Core data of the KFS.

        public void Deserialize()
        {
            WmCd = new WmCoreData();
            KwsCdList = new SortedDictionary<UInt64, KwsCoreData>();
        }
    }

    /// <summary>
    /// Methods used to import and export workspaces and folders.
    /// </summary>
    public static class WmKwsImportExport
    {
        /// <summary>
        /// Import a workspace list from the file specified.
        /// </summary>
        public static void ImportKwsListFromFile(String path)
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(path);
            ImportKwsListFromXmlDoc(doc);
        }

        /// <summary>
        /// Import a workspace list from the XML document specified.
        /// </summary>
        public static void ImportKwsListFromXmlDoc(XmlDocument doc)
        {
            UInt32 version = UInt32.Parse(doc.DocumentElement.GetAttribute("version"));
            if (version > 1) throw new Exception("unsupported KwsExport version ('" + version + "')");

            // Import all nodes.
            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                XmlElement el = node as XmlElement;
                if (el == null) throw new Exception("Corrupted document (Node is not an XmlElement).");
                else if (el.Name == "Kws") ImportKws(XmlToKwsCredentials(el));
                else if (el.Name == "KwsBrowser") ImportFolderList(XmlToFolderList(el));
                else throw new Exception("Corrupted document (unknown node '" + el.Name + "'.");
            }

            // Serialize the WM since we may have created workspaces.
            Wm.Serialize();
        }

        /// <summary>
        /// Import the workspace specified.
        /// </summary>
        public static void ImportKws(KwsCredentials creds)
        {
            Workspace kws = Wm.GetKwsByExternalID(creds.KcdID, creds.ExternalID);
            if (kws != null) ImportExistingKws(kws, creds);
            else ImportNewKws(creds);
        }

        /// <summary>
        /// "Import" a workspace that already exists in the KWM.
        /// </summary>
        private static void ImportExistingKws(Workspace kws, KwsCredentials creds)
        {
            KwsTask task = kws.Cd.CurrentTask;
            KLogging.Log("Import of existing workspace " + kws.InternalID + " with task " + task + " requested.");

            // We can only import workspaces that are stopped or working 
            // offline.
            if (task != KwsTask.Stop && task != KwsTask.WorkOffline)
            {
                KLogging.Log("Skipping import due to incompatible task.");
                return;
            }

            // Update the credentials unless they were already accepted.
            if (kws.Cd.KcdState.LoginResult != KwsLoginResult.Accepted)
            {
                KLogging.Log("Updating workspace credentials.");
                creds.PublicFlag = kws.Cd.Credentials.PublicFlag;
                kws.Cd.Credentials = creds;
            }

            // Make the workspace work online.
            kws.Sm.RequestTaskSwitch(KwsTask.WorkOnline);

            // The workspace state has changed.
            kws.OnStateChange(WmStateChange.Permanent);
        }

        /// <summary>
        /// Import or join a workspace that does not exist in the KWM.
        /// </summary>
        private static void ImportNewKws(KwsCredentials creds)
        {
            KLogging.Log("Importing new workspace " + creds.KwsName + ".");

            // Create the workspace.
            Workspace kws = Wm.CreateWorkspace(creds);

            // Set its main status.
            kws.Cd.MainStatus = KwsMainStatus.Good;

            // Make the workspace work online.
            kws.Sm.RequestTaskSwitch(KwsTask.WorkOnline);
        }

        /// <summary>
        /// Import the workspace folder list specified.
        /// </summary>
        public static void ImportFolderList(List<WmKwsFolder> folderList)
        {
            // Import the missing folders. Update the ET blob of existing folders.
            foreach (WmKwsFolder f in folderList)
            {
                bool foundFlag = false;
                foreach (WmKwsFolder g in Wm.Cd.KwsFolderList)
                {
                    if (g.FullPath == f.FullPath)
                    {
                        foundFlag = true;
                        g.EtBlob = f.EtBlob;
                        break;
                    }
                }
                if (!foundFlag) Wm.Cd.KwsFolderList.Add(f);
            }

            // The WM state has changed.
            Wm.OnStateChange(WmStateChange.Permanent);
        }

        /// <summary>
        /// Export the given KWS to the specified path. Set kwsID to 0
        /// to export all the workspaces.
        /// </summary>
        public static void ExportKws(String path, UInt64 kwsID)
        {
            XmlDocument doc = new XmlDocument();
            XmlNode node = doc.CreateNode(XmlNodeType.XmlDeclaration, "", "");
            doc.AppendChild(node);

            XmlElement ke = doc.CreateElement("TeamboxExport");
            ke.SetAttribute("version", "1");
            doc.AppendChild(ke);

            if (kwsID == 0)
            {
                // Export all workspaces.
                foreach (Workspace kws in Wm.KwsTree.Values)
                    KwsCredentialsToXml(kws.Cd.Credentials, doc, ke);

                // Export all folders.
                FolderListToXml(Wm.Cd.KwsFolderList, doc, ke);
            }

            else
            {
                // Export only the specified workspace.
                Workspace kws = Wm.GetKwsByInternalID(kwsID);
                KwsCredentialsToXml(kws.Cd.Credentials, doc, ke);
            }

            using (Stream s = File.Open(path, FileMode.Create)) doc.Save(s);
        }

        /// <summary>
        /// Store the workspace credentials in the element specified. Nothing 
        /// is exported if the credentials are invalid.
        /// </summary>
        private static void KwsCredentialsToXml(KwsCredentials c, XmlDocument doc, XmlElement parent)
        {
            if (c.KcdAddress == "" || c.ExternalID == 0) return;

            XmlElement el = doc.CreateElement("Kws");
            parent.AppendChild(el);

            el.SetAttribute("version", KwsCredentials.ExportVersion.ToString());
            KwmXml.CreateXmlElement(doc, el, "KcdAddress", c.KcdAddress);
            if (c.KwmoAddress != "") KwmXml.CreateXmlElement(doc, el, "KwmoAddress", c.KwmoAddress);
            KwmXml.CreateXmlElement(doc, el, "ExternalID", c.ExternalID.ToString());
            KwmXml.CreateXmlElement(doc, el, "EmailID", c.EmailID);
            if (c.KwsName != "") KwmXml.CreateXmlElement(doc, el, "KwsName", c.KwsName);
            if (c.UserName != "") KwmXml.CreateXmlElement(doc, el, "UserName", c.UserName);
            if (c.UserEmailAddress != "") KwmXml.CreateXmlElement(doc, el, "UserEmailAddress", c.UserEmailAddress);
            if (c.InviterName != "") KwmXml.CreateXmlElement(doc, el, "InviterName", c.InviterName);
            if (c.InviterEmailAddress != "") KwmXml.CreateXmlElement(doc, el, "InviterEmailAddress", c.InviterEmailAddress);
            if (c.UserID != 0) KwmXml.CreateXmlElement(doc, el, "UserID", c.UserID.ToString());
            if (c.Ticket != null) KwmXml.CreateXmlElement(doc, el, "Ticket", Convert.ToBase64String(c.Ticket));
            if (c.Pwd != "") KwmXml.CreateXmlElement(doc, el, "Pwd", c.Pwd);
            if (c.Flags != 0) KwmXml.CreateXmlElement(doc, el, "Flags", c.Flags.ToString());
            if (c.FolderPath != "") KwmXml.CreateXmlElement(doc, el, "FolderPath", c.FolderPath);
            if (c.EtBlob != null) KwmXml.CreateXmlElement(doc, el, "EtBlob", Convert.ToBase64String(c.EtBlob));
        }

        /// <summary>
        /// Extract the workspace credentials from the XML element specified.
        /// </summary>
        private static KwsCredentials XmlToKwsCredentials(XmlElement el)
        {
            KwsCredentials c = new KwsCredentials();

            int version = Int32.Parse(el.GetAttribute("version"));
            if (version > KwsCredentials.ExportVersion)
                throw new Exception("unsupported kws version ('" + version + "')");

            c.KcdAddress = KwmXml.GetXmlChildValue(el, "KcdAddress", "");
            c.KwmoAddress = KwmXml.GetXmlChildValue(el, "KwmoAddress", "");
            c.ExternalID = UInt64.Parse(KwmXml.GetXmlChildValue(el, "ExternalID", "0"));
            c.EmailID = KwmXml.GetXmlChildValue(el, "EmailID", "");
            c.KwsName = KwmXml.GetXmlChildValue(el, "KwsName", "");
            c.UserName = KwmXml.GetXmlChildValue(el, "UserName", "");
            c.UserEmailAddress = KwmXml.GetXmlChildValue(el, "UserEmailAddress", "");
            c.InviterName = KwmXml.GetXmlChildValue(el, "InviterName", "");
            c.InviterEmailAddress = KwmXml.GetXmlChildValue(el, "InviterEmailAddress", "");
            c.UserID = UInt32.Parse(KwmXml.GetXmlChildValue(el, "UserID", "0"));
            c.Ticket = Convert.FromBase64String(KwmXml.GetXmlChildValue(el, "Ticket", ""));
            c.Pwd = KwmXml.GetXmlChildValue(el, "Pwd", "");
            c.Flags = UInt32.Parse(KwmXml.GetXmlChildValue(el, "Flags", "0"));
            c.FolderPath = KwmXml.GetXmlChildValue(el, "FolderPath", "");
            c.EtBlob = Convert.FromBase64String(KwmXml.GetXmlChildValue(el, "EtBlob", ""));

            if (version < 4)
            {
                XmlElement kasIDElem = KwmXml.GetXmlChildElement(el, "KasID");
                if (kasIDElem == null) throw new Exception("KasID element not present");
                c.KcdAddress = KwmXml.GetXmlChildValue(kasIDElem, "Host", "");
                c.PublicFlag = bool.Parse(KwmXml.GetXmlChildValue(el, "PublicFlag", "False"));
                c.SecureFlag = bool.Parse(KwmXml.GetXmlChildValue(el, "SecureFlag", "False"));
            }

            // Normalize the data.
            if (c.Ticket != null && c.Ticket.Length == 0) c.Ticket = null;
            if (c.EtBlob != null && c.EtBlob.Length == 0) c.EtBlob = null;

            // Validate.
            if (c.KcdAddress == "" || c.ExternalID == 0)
                throw new Exception("invalid kws credentials");

            return c;
        }

        /// <summary>
        /// Store the folder list in the element specified.
        /// </summary>
        private static void FolderListToXml(List<WmKwsFolder> folderList, XmlDocument doc, XmlElement parent)
        {
            XmlElement be = doc.CreateElement("KwsBrowser");
            be.SetAttribute("version", "2");
            parent.AppendChild(be);
            foreach (WmKwsFolder f in folderList)
            {
                XmlElement fe = doc.CreateElement("Folder");
                be.AppendChild(fe);
                fe.SetAttribute("name", f.FullPath);
                if (f.EtBlob != null) fe.SetAttribute("EtBlob", Convert.ToBase64String(f.EtBlob));
            }
        }

        /// <summary>
        /// Extract the folder list from the XML element specified.
        /// </summary>
        private static List<WmKwsFolder> XmlToFolderList(XmlElement el)
        {
            int version = Int32.Parse(el.GetAttribute("version"));
            if (version > 2) throw new Exception("unsupported browser version ('" + version + "')");

            List<WmKwsFolder> folderList = new List<WmKwsFolder>();

            foreach (XmlNode node in el.ChildNodes)
            {
                XmlElement fe = node as XmlElement;
                if (fe == null) throw new Exception("expected folder node");
                
                WmKwsFolder f = new WmKwsFolder();
                folderList.Add(f);

                f.FullPath = fe.GetAttribute("name");
                if (version == 2) fe.GetAttribute("EtBlob");
            }

            return folderList;
        }
    }
}