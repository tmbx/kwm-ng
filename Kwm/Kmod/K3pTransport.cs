using kcslib;
using kwmlib;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace kwm
{
    /// <summary>
    /// Manage the mechanic of sending and receiving K3P messages.
    /// </summary>
    public class K3pTransport
    {
        enum InState
        {
            NoMsg,
            RecvHdr,
            RecvStrSize,
            RecvIns,
            RecvInt,
            RecvStr,
            Received
        };

        enum OutState
        {
            NoPacket,
            Sending,
        };

        private InState inState = InState.NoMsg;
        private K3pElement inMsg;
        private byte[] inBuf;
        private int inPos;
        private OutState outState = OutState.NoPacket;
        private byte[] outBuf;
        private int outPos;
        private Socket sock;

        public bool isReceiving
        {
            get { return (inState != InState.NoMsg); }
        }
        public bool doneReceiving
        {
            get { return (inState == InState.Received); }
        }
        public bool isSending
        {
            get { return (outState != OutState.NoPacket); }
        }
        public void reset()
        {
            flushRecv();
            flushSend();
            sock = null;
        }

        public void flushRecv() { inState = InState.NoMsg; }
        public void flushSend() { outState = OutState.NoPacket; }

        public K3pTransport(Socket s)
        {
            sock = s;
        }

        public void beginRecv()
        {
            inState = InState.RecvHdr;
            inBuf = new byte[3];
            inPos = 0;
        }

        public K3pElement getRecv()
        {
            Debug.Assert(doneReceiving);
            K3pElement m = inMsg;
            flushRecv();
            return m;
        }

        public void sendMsg(K3pMsg msg)
        {
            outState = OutState.Sending;
            MemoryStream s = new MemoryStream();
            msg.ToStream(s);
            outBuf = s.ToArray();
            outPos = 0;
        }

        public void doXfer()
        {
            bool loop = true;

            while (loop)
            {
                loop = false;

                if (inState == InState.RecvHdr)
                {
                    int r = KSocket.SockRead(sock, inBuf, inPos, inBuf.Length - inPos);

                    if (r > 0)
                    {
                        loop = true;
                        inPos += r;

                        if (inPos == inBuf.Length)
                        {
                            inMsg = new K3pElement();

                            inMsg.ParseType(inBuf);

                            switch (inMsg.Type)
                            {
                                case K3pElement.K3pType.INS:
                                    inState = InState.RecvIns;
                                    inPos = 0;
                                    inBuf = new byte[8];
                                    break;
                                case K3pElement.K3pType.INT:
                                    inState = InState.RecvInt;
                                    inPos = 0;
                                    inBuf = new byte[20];
                                    break;
                                case K3pElement.K3pType.STR:
                                    inState = InState.RecvStrSize;
                                    inBuf = new byte[20];
                                    inPos = 0;
                                    break;
                            }
                        }
                    }
                }

                if (inState == InState.RecvIns)
                {
                    int r = KSocket.SockRead(sock, inBuf, inPos, inBuf.Length - inPos);

                    if (r > 0)
                    {
                        loop = true;
                        inPos += r;

                        if (inPos == inBuf.Length)
                        {
                            inMsg.ParseIns(inBuf);
                            inState = InState.Received;
                        }
                    }
                }

                if (inState == InState.RecvInt || inState == InState.RecvStrSize)
                {
                    int r = KSocket.SockRead(sock, inBuf, inPos, 1);

                    if (r > 0)
                    {
                        loop = true;
                        inPos += r;

                        if ((char)inBuf[inPos - 1] == '>')
                        {
                            byte[] tmpInBuf = new byte[inPos];
                            for (int i = 0; i < inPos; i++)
                            {
                                tmpInBuf[i] = inBuf[i];
                            }
                            inMsg.ParseInt(tmpInBuf);
                            if (inState == InState.RecvStrSize)
                            {
                                inState = InState.RecvStr;
                                inBuf = new byte[inMsg.Int];
                                inPos = 0;

                                if (inMsg.Int == 0)
                                {
                                    inMsg.ParseStr(inBuf);
                                    inState = InState.Received;
                                }
                            }
                            else
                                inState = InState.Received;
                        }
                        else if (inPos == inBuf.Length)
                        {
                            throw new K3pException("Expecting a '>' to end an INT in k3p message");
                        }
                    }
                }

                if (inState == InState.RecvStr)
                {
                    int r = KSocket.SockRead(sock, inBuf, inPos, inBuf.Length - inPos);

                    if (r > 0)
                    {
                        loop = true;
                        inPos += r;

                        if (inPos == inBuf.Length)
                        {
                            inMsg.ParseStr(inBuf);
                            inState = InState.Received;
                        }
                    }
                }

                if (outState == OutState.Sending)
                {
                    int r = KSocket.SockWrite(sock, outBuf, outPos, outBuf.Length - outPos);

                    if (r > 0)
                    {
                        loop = true;
                        outPos += r;

                        if (outPos == outBuf.Length)
                        {
                            outState = OutState.NoPacket;
                            break;
                        }
                    }
                }
            }
        }
    }
}