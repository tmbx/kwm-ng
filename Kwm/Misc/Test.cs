using kcslib;
using kwmlib;
using System.Diagnostics;
using System;

namespace kwm
{
    public class TestSuite
    {
        public static void TestPath()
        {
            Debug.Assert(KFile.GetUnixFilePath("", true) == "");
            Debug.Assert(KFile.GetUnixFilePath("", false) == "");
            Debug.Assert(KFile.GetUnixFilePath("a", true) == "a/");
            Debug.Assert(KFile.GetUnixFilePath("a/", true) == "a/");
            Debug.Assert(KFile.GetUnixFilePath("a", false) == "a");
            Debug.Assert(KFile.GetUnixFilePath("a/", false) == "a");
            Debug.Assert(KFile.GetUnixFilePath("a\\b", true) == "a/b/");

            String[] SA = KFile.SplitRelativePath("a/b/");
            Debug.Assert(SA.Length == 2);
            Debug.Assert(SA[0] == "a");
            Debug.Assert(SA[1] == "b");

            Debug.Assert(KFile.DirName("") == "");
            Debug.Assert(KFile.DirName("a") == "");
            Debug.Assert(KFile.DirName("a/") == "a/");
            Debug.Assert(KFile.DirName("a/b") == "a/");

            Debug.Assert(KFile.BaseName("") == "");
            Debug.Assert(KFile.BaseName("a") == "a");
            Debug.Assert(KFile.BaseName("a/") == "");
            Debug.Assert(KFile.BaseName("a/b") == "b");

            Debug.Assert(KFile.StripTrailingDelim("") == "");
            Debug.Assert(KFile.StripTrailingDelim("/") == "");
            Debug.Assert(KFile.StripTrailingDelim("a") == "a");
            Debug.Assert(KFile.StripTrailingDelim("a/") == "a");
        }
    }
}