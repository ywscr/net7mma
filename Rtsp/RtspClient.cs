﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using Media.Rtcp;
using Media.Rtp;
using Media.Sdp;
using System.Threading;

namespace Media.Rtsp
{
    /// <summary>
    /// Communicates with an RtspServer to setup a RtpClient.    
    /// </summary>
    public class RtspClient
    {
        #region Nested Types

        public class RtspListenerException : Exception
        { 
            //Might choose to put a RtspRequest here 
            public RtspListenerException(string message) : base(message){}
            public RtspListenerException(string message, Exception inner) : base(message, inner) { }
        }

        #endregion

        #region Fields

        /// <summary>
        /// The location the media
        /// </summary>
        Uri m_RtspLocation, m_Location;

        /// <summary>
        /// The buffer this client uses for all requests 4MB
        /// </summary>
        byte[] m_Buffer = new byte[RtspMessage.MaximumLength];

        /// <summary>
        /// The remote IPAddress to which the Location resolves via Dns
        /// </summary>
        IPAddress m_RemoteIP;

        /// <summary>
        /// The remote RtspEndPoint
        /// </summary>
        EndPoint m_RemoteRtsp;

        /// <summary>
        /// The socket used for Rtsp Communication
        /// </summary>
        Socket m_RtspSocket;

        /// <summary>
        /// The network stream of the RtspSocket
        /// </summary>
        NetworkStream m_RtspStream;

        /// <summary>
        /// The protcol in which Rtsp data will be transpored from the server
        /// </summary>
        ProtocolType m_RtpProtocol;

        /// <summary>
        /// The last method used in a request to the RtspServer
        /// </summary>
        RtspMessage.RtspMethod m_LastMethod;

        /// <summary>
        /// The session description associated with the media at Location
        /// </summary>
        SessionDescription m_SessionDescription;

        /// <summary>
        /// Need to seperate counters
        /// </summary>
        int m_Sent, m_Recieved,
            m_RtspTimeout, m_RtspPort, m_Ssrc, m_Seq, m_CSeq,
            m_ClientRtpPort, m_ClientRtcpPort;

        List<RtspMessage.RtspMethod> m_SupportedMethods = new List<RtspMessage.RtspMethod>();

        string m_UserAgent = "ASTI RTP Client", m_SessionId, m_TransportMode, m_Range;

        internal RtpClient m_RtpClient;

        Timer m_Timer;

        #endregion

        #region Properties

        /// <summary>
        /// The identifier of the source media given after a setup request of the RtspClient
        /// </summary>
        public int SynchronizationSourceIdentifier { get { return m_Ssrc; } }

        /// <summary>
        /// The location to the Media on the Rtsp Server
        /// </summary>
        public Uri Location
        {
            get { return m_Location; }
            internal set
            {
                //Parse 
                m_Location = value;
                try
                {
                    m_RemoteIP = System.Net.Dns.GetHostAddresses(m_Location.Host)[0];
                }
                catch (Exception ex)
                {
                    throw new RtspListenerException("Could not resolve host from the given location", ex);
                }

                m_RtspPort = m_Location.Port;

                if (m_RtspPort == -1) m_RtspPort = RtspMessage.DefaultPort;

                m_RemoteRtsp = new IPEndPoint(m_RemoteIP, m_RtspPort);                
            }
        }

        /// <summary>
        /// Indicates if the RtspListener is connected to the remote host
        /// </summary>
        public bool Connected { get { return m_RtspSocket != null && m_RtspSocket.Connected; } }

        /// <summary>
        /// The network credential to utilize in RtspRequests
        /// </summary>
        public NetworkCredential Credential { get; set; }

        /// <summary>
        /// Indicates if the RtspClient has started listening for RtpData
        /// </summary>
        public bool Listening { get { return m_RtspSocket != null && m_RtspSocket.Connected && m_RtpClient != null; } }

        /// <summary>
        /// The amount of bytes sent by the RtspClient
        /// </summary>
        public int BytesSent { get { return m_Sent; } }

        /// <summary>
        /// The amount of bytes recieved by the RtspClient
        /// </summary>
        public int BytesRecieved { get { return m_Recieved; } }

        /// <summary>
        /// The current SequenceNumber of the RtspClient
        /// </summary>
        public int ClientSequencNumber { get { return m_CSeq; } }

        /// <summary>
        /// The "seqno=" recieved with the play request.
        /// </summary>
        public int StreamStartSequencNumber { get { return m_Seq; } }

        /// <summary>
        /// Increments and returns the current SequenceNumber
        /// </summary>
        internal int NextSequenceNumber { get { return ++m_CSeq; } }

        /// <summary>
        /// Gets the SessionDescription provided by the server for the media at <see cref="Location"/>
        /// </summary>
        public SessionDescription SessionDescription { get { return m_SessionDescription; } internal set { m_SessionDescription = value; } }

        /// <summary>
        /// Gets the methods supported by the server recieved in the options request.
        /// </summary>
        public Rtsp.RtspMessage.RtspMethod[] SupportedMethods { get { return m_SupportedMethods.ToArray(); } }

        /// <summary>
        /// The RtpClient associated with this RtspClient
        /// </summary>
        public RtpClient Client { get { return m_RtpClient; } }

        /// <summary>
        /// Gets or Sets the ReadTimeout of the underlying NetworkStream / Socket
        /// </summary>
        public int ReadTimeout { get { return m_RtspStream.ReadTimeout; } set { m_RtspStream.ReadTimeout = value; } }

        /// <summary>
        /// Gets or Sets the WriteTimeout of the underlying NetworkStream / Socket
        /// </summary>
        public int WriteTimeout { get { return m_RtspStream.WriteTimeout; } set { m_RtspStream.WriteTimeout = value; } }

        /// <summary>
        /// The UserAgent sent with every RtspRequest
        /// </summary>
        public string UserAgent { get { return m_UserAgent; } set { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentNullException("UserAgent cannot consist of only null or whitespace."); m_UserAgent = value; } }

        #endregion

        #region Constructor

        static RtspClient()
        {
            if (!UriParser.IsKnownScheme(RtspMessage.ReliableTransport))
                UriParser.Register(new HttpStyleUriParser(), RtspMessage.ReliableTransport, 554);

            if (!UriParser.IsKnownScheme(RtspMessage.UnreliableTransport))
                UriParser.Register(new HttpStyleUriParser(), RtspMessage.UnreliableTransport, 554);
        }

        /// <summary>
        /// Creates a RtspClient on a non standard Rtsp Port
        /// </summary>
        /// <param name="location">The location of the media</param>
        /// <param name="rtspPort">The port to the RtspServer is listening on</param>
        public RtspClient(Uri location)
        {

            if (!location.IsAbsoluteUri) throw new ArgumentException("Must be absolute", "location");
            if (!(location.Scheme == RtspMessage.ReliableTransport || location.Scheme == RtspMessage.UnreliableTransport)) throw new ArgumentException("Scheme must be rtsp or rtspu", "location");

            Location = location;
            
            m_RtpProtocol = ProtocolType.Udp;                        
        }        

        /// <summary>
        /// Creates a new RtspClient from the given uri in string form.
        /// E.g. 'rtsp://somehost/sometrack/
        /// </summary>
        /// <param name="location">The string which will be parsed to obtain the Location</param>
        public RtspClient(string location) : this(new Uri(location)) { }

        ~RtspClient()
        {
            if (m_RtpClient != null)
            {
                m_RtpClient.Disconnect();
            }
            m_RtpClient = null;
        }

        #endregion

        #region Methods

        public void StartListening()
        {
            if (Listening) return;
            try
            {
                Connect();
                SendOptions();
                SendDescribe();
                SendSetup();
                SendPlay();

                //It appears the RtspClient may need a worker thread to monitor the bytes recieved... if things stop coming in then StopListening must be called.
            }
            catch
            {
                throw;
            }
        }

        public void StopListening()
        {
            if (!Listening) return;
            if(m_Timer != null) m_Timer.Dispose();
            //Send the RtcpGoodbye
            m_RtpClient.Disconnect();
            m_RtpClient = null;
        }

        public void Connect()
        {
            try
            {
                m_RtspSocket = new Socket(m_RemoteIP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                m_RtspSocket.Connect(m_RemoteRtsp);
                m_RtspStream = new NetworkStream(m_RtspSocket);
                m_RtspStream.ReadTimeout = 2000;
            }
            catch(Exception ex)
            {
                throw new RtspListenerException("Could not connect to remote host", ex);
            }
        }

        public void Disconnect()
        {
            try
            {
                //Determine if we need to do anything
                if (Listening && m_LastMethod != RtspMessage.RtspMethod.UNKNOWN && m_LastMethod != RtspMessage.RtspMethod.TEARDOWN && m_LastMethod > RtspMessage.RtspMethod.OPTIONS)
                {
                    StopListening();                    
                }

                if (m_RtspStream != null)
                {
                    //Close our RtspStream
                    m_RtspStream.Dispose();
                }

                if (m_RtspSocket != null)
                {

                    m_RtspSocket.Disconnect(true);
                    m_RtspSocket.Dispose();
                    m_RtspSocket = null;
                }

                m_RtpClient = null;
            }
            catch { }
        }

        #endregion

        #region Rtsp        

        internal RtspResponse SendRtspRequest(RtspRequest request)
        {
            try
            {

                if (!Connected) throw new RtspListenerException("You must first Connect before sending a request");

                if (request[RtspMessage.RtspHeaders.UserAgent] == null)
                {
                    request.SetHeader(RtspMessage.RtspHeaders.UserAgent, m_UserAgent);
                }

                if (Credential != null)
                {
                    //Encoding should be a property on the Listener which defaults to utf8
                    request.SetHeader(RtspMessage.RtspHeaders.Authroization, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Credential.UserName + ':' + Credential.Password)));
                }

                if (m_SessionId != null)
                {
                    request.SetHeader(RtspMessage.RtspHeaders.Session, m_SessionId);
                }

                request.CSeq = NextSequenceNumber;

                m_LastMethod = request.Method;

                byte[] buffer = request.ToBytes();

                lock (m_RtspStream)
                {

                    m_RtspStream.Write(buffer, 0, buffer.Length);

                    m_RtspStream.Flush();
                }
                
                m_Sent += buffer.Length;

                //m_Sent += m_RtspSocket.Send(request.ToBytes());

                //int rec = m_RtspSocket.Receive(m_Buffer);

                int rec;
            Rece:
                rec = 0;
                lock (m_RtspStream)
                {
                    rec = m_RtspStream.Read(m_Buffer, 0, m_Buffer.Length);
                }

                if (rec > 0)
                {
                    m_Recieved += rec;

                    try
                    {

                        RtspResponse result = new RtspResponse(m_Buffer);
                        return result;
                    }
                    catch (RtspMessage.RtspMessageException)
                    {
                        //OnRtpRecieve(new RtpPacket(new ArraySegment<byte>(m_Buffer, 0, rec)));
                        goto Rece;
                        //return null;
                    }
                }

                return null;
            }
            catch (RtspListenerException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RtspListenerException("An error occured during the request", ex);
            }
        }

        public RtspResponse SendOptions()
        {
            RtspRequest options = new RtspRequest(RtspMessage.RtspMethod.OPTIONS, Location);
            RtspResponse response = SendRtspRequest(options);

            if (response == null || response.StatusCode != RtspResponse.ResponseStatusCode.OK) throw new RtspListenerException("Unable to get options");

            m_SupportedMethods.Clear();

            string publicMethods = response[RtspMessage.RtspHeaders.Public];

            if (string.IsNullOrEmpty(publicMethods)) return response;

            foreach (string method in publicMethods.Split(','))
            {
                m_SupportedMethods.Add((RtspMessage.RtspMethod)Enum.Parse(typeof(Rtsp.RtspMessage.RtspMethod), method));
            }

            return response;
        }

        /// <summary>
        /// Assigns the SessionDescription returned from the server
        /// </summary>
        /// <returns></returns>
        public RtspResponse SendDescribe()
        {
            RtspRequest describe = new RtspRequest(RtspMessage.RtspMethod.DESCRIBE, Location);
            describe.SetHeader(RtspMessage.RtspHeaders.Accept, "application/sdp");

            RtspResponse response = SendRtspRequest(describe);

            if (response.StatusCode != RtspResponse.ResponseStatusCode.OK) throw new RtspListenerException("Unable to describe media: " + response.m_FirstLine);

            try
            {
                m_SessionDescription = new Sdp.SessionDescription(response.Body);
            }            
            catch (SessionDescription.SessionDescriptionException ex)
            {
                throw new RtspListenerException("Invalid Session Description", ex);
            }

            //The media Uri is combined with the media description of the track to control ?
            //Location = new Uri(Location.ToString() + '/' + m_SessionDescription.MediaDesciptions[0].GetAtttribute("control"));            

            return response;
        }

        public RtspResponse SendTeardown() 
        {            
            RtspResponse response = null;
            try
            {
                RtspRequest teardown = new RtspRequest(RtspMessage.RtspMethod.TEARDOWN, Location);
                response = SendRtspRequest(teardown);
                return response;
            }
            catch (RtspClient.RtspListenerException)
            {
                //During the teardown we sometimes misuse the buffer
                //In this case the packet was a RtpPacket or RtcpPacket we should have the RtspReqeust next
                //System.Diagnostics.Debugger.Break();
                return response;
            }
            catch
            {
                throw;
            }
            finally
            {
                m_SessionId = null;
                m_CSeq = 0;
            }
        }

        public RtspResponse SendSetup()
        {
            try
            {
                RtspRequest setup = new RtspRequest(RtspMessage.RtspMethod.SETUP, Location);

                if (m_RtpProtocol == ProtocolType.Tcp) //m_SessionDescription.MediaDesciptions[0].MediaProtocol.Contains("TCP")
                {
                    setup.SetHeader(RtspMessage.RtspHeaders.Transport, "RTP/AVP/TCP;unicast;interleaved=0-1");
                }
                else
                {
                    m_ClientRtpPort = Utility.FindOpenUDPPort(15000);
                    m_ClientRtcpPort = m_ClientRtpPort + 1;
                    m_RtpClient = RtpClient.Receiever(m_RemoteIP, m_ClientRtpPort, m_ClientRtcpPort);//SHould share the buffer?
                    setup.SetHeader(RtspMessage.RtspHeaders.Transport, m_SessionDescription.MediaDesciptions[0].MediaProtocol + ";unicast;client_port=" + m_ClientRtpPort + '-' + m_ClientRtcpPort);
                }

                RtspResponse response = SendRtspRequest(setup);

                if (response.StatusCode == RtspResponse.ResponseStatusCode.SessionNotFound || ((int)(response.StatusCode)) == 454)
                {
                    //StopListening(); 
                    //return null;
                    SendTeardown();                    
                    SendDescribe();
                    return SendSetup();
                }

                if (response.StatusCode != RtspResponse.ResponseStatusCode.OK) throw new RtspListenerException("Unable to setup media: " + response.m_FirstLine);

                string sessionHeader = response[RtspMessage.RtspHeaders.Session];

                //If there is a session header it may contain the option timeout
                if (!String.IsNullOrEmpty(sessionHeader))
                {
                    if (sessionHeader.Contains(';'))
                    {
                        string[] temp = sessionHeader.Split(';');

                        m_SessionId = temp[0];

                        //If there is a timeout we may want to setup a timer on these seconds to send a GET_PARAMETER
                        m_RtspTimeout = Convert.ToInt32(temp[1].Replace("timeout=", string.Empty));
                    }
                    else
                    {
                        m_SessionId = response[RtspMessage.RtspHeaders.Session];
                        m_RtspTimeout = 60000;
                    }

                }

                string transportHeader = response[RtspMessage.RtspHeaders.Transport];

                //We need a transportHeader
                if (String.IsNullOrEmpty(transportHeader)) throw new RtspClient.RtspListenerException("Cannot setup media, Invalid Transport Header in Rtsp Response");

                //Get the parts of information from the transportHeader
                string[] parts = transportHeader.Split(';');

                ///The transport header contains the following information 
                foreach (string part in parts)
                {
                    if (part.Equals("RTP/AVP")) m_RtpProtocol = ProtocolType.Udp;
                    else if (part.Equals("RTP/AVP/UDP")) m_RtpProtocol = ProtocolType.Udp;
                    else if (part.Equals("RTP/AVP/TCP")) m_RtpProtocol = ProtocolType.Tcp;
                    //else if (part == "unicast") {  }
                    else if (part.StartsWith("client_port="))
                    {
                        //m_ClientPort = Convert.ToInt32(part.Replace("client_port=", string.Empty));  
                        //Maybe should ensure they match what we sent to the server
                    }
                    else if (part.StartsWith("server_port="))
                    {
                        //m_ServerPort = Convert.ToInt32(part.Replace("Server_port=", string.Empty));
                        string[] ports = part.Replace("server_port=", string.Empty).Split('-');

                        //This is not in any RFC including 2326
                        //If there is not a port pair then this must be a tcp response
                        if (ports.Length == 1)
                        {
                            //THIS IS INDICATING A TCP Transport
                            m_RtpProtocol = ProtocolType.Tcp;

                            //We must use interleaved
                            m_RtpProtocol = ProtocolType.Tcp;

                            m_RtpClient = RtpClient.Interleaved(m_RtspSocket);

                            //Recurse call to ensure propper setup
                            return SendSetup();

                        }
                        else
                        {
                            m_RtpProtocol = ProtocolType.Udp;
                                                        
                            m_RtpClient.m_ServerRtpPort = Convert.ToInt32(ports[0]);
                            m_RtpClient.m_ServerRtcpPort = Convert.ToInt32(ports[1]);
                        }
                    }
                    else if (part.StartsWith("mode="))
                    {
                        m_TransportMode = part.Replace("mode=", string.Empty).Trim();
                    }
                    else if (part.StartsWith("ssrc="))
                    {
                        string tPart = part.Replace("ssrc=", string.Empty).Trim();

                        if (!Int32.TryParse(tPart, out m_Ssrc))
                        {
                            m_Ssrc = int.Parse(tPart, System.Globalization.NumberStyles.HexNumber);
                        }
                    }
                }

                m_RtpClient.Connect();

                m_RtpClient.m_RtpSSRC = (uint)m_Ssrc;

                return response;
            }
            catch (RtspListenerException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RtspListenerException("Unable not setup media: ", ex);
            }
        }

        //Needs facilites for sending play for multiple tracks...

        public RtspResponse SendPlay()
        {
            //SHould check if already listenign

            try
            {
                RtspRequest play = new RtspRequest(RtspMessage.RtspMethod.PLAY, Location);

                play.SetHeader(RtspMessage.RtspHeaders.Range, "npt=" + m_Range + '-');

                RtspResponse response = SendRtspRequest(play);

                if (response.StatusCode != RtspResponse.ResponseStatusCode.OK) throw new RtspListenerException("Unable to play media: " + response.m_FirstLine);

                string rtpInfo = response[RtspMessage.RtspHeaders.RtpInfo];

                if (!string.IsNullOrEmpty(rtpInfo))
                {
                    string[] pieces = rtpInfo.Split(',');
                    foreach (string piece in pieces)
                    {
                        if (piece.StartsWith("url="))
                        {
                            //Location = new Uri(piece.Replace("url=", string.Empty));
                            m_RtspLocation = new Uri(piece.Replace("url=", string.Empty).Trim());
                        }
                        else if (piece.StartsWith("seqno="))
                        {
                            m_Seq = Convert.ToInt32(piece.Replace("seqno=", string.Empty).Trim());
                        }
                    }
                }//should throw not found RtpInfo

                string rangeString = response[RtspMessage.RtspHeaders.Range];

                if (!string.IsNullOrEmpty(rangeString))
                {
                    m_Range = rangeString.Replace("npt=", string.Empty).Replace("-", string.Empty).Trim();
                }//Should throw if RtpInfo was present, Range requried RtpInfo

                //Connect the RtpClient
                m_RtpClient.Connect();

                //If there is a timeout ensure it gets utilized
                if (m_RtspTimeout != 0)
                {
                    m_Timer = new Timer(new TimerCallback(SendGetParameter), this, m_RtspTimeout * 1000 / 2, m_RtspTimeout);
                }

                return response;
            }
            catch (RtspListenerException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RtspListenerException("Unable to play media", ex);
            }
        }

        internal void SendGetParameter(object state)
        {
            try { SendGetParameter(null); }
            catch
            {
                if (m_Timer != null)
                {
                    m_Timer.Dispose(); m_Timer = null;
                }
            }
        }

        public RtspResponse SendGetParameter(string body = null)
        {
            try
            {
                RtspRequest get = new RtspRequest(RtspMessage.RtspMethod.GET_PARAMETER, Location);                
                if (string.IsNullOrWhiteSpace(body)) get.Body = body;
                RtspResponse response = SendRtspRequest(get);
                if (response == null)
                {
                    //m_Timer.Dispose();
                    //m_Timer = null;
                    return null;
                }
                if(response.StatusCode != RtspResponse.ResponseStatusCode.OK) throw new RtspListenerException("Unsuccessful GET_PARAMETER: " + response.m_FirstLine);             
                return response;
            }
            catch (RtspListenerException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion
    }
}