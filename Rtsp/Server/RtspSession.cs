﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Net;
using Media.Rtp;
using Media.Rtcp;
using System.Threading;

namespace Media.Rtsp
{
    /// <summary>
    /// Sends packets from a source RtspStream to a remote RtspClient
    /// </summary>
    internal class RtspSession
    {
        #region Fields

        internal RtspServer m_Server;
        
        internal Guid m_Id = Guid.NewGuid();

        //Recieved times
        internal DateTime m_LastRtspRequestRecieved;

        //Sdp
        internal string m_SessionId, m_SDPVersion;

        internal Media.Sdp.SessionDescription m_SessionDescription;

        //The method from the last request
        internal RtspRequest m_LastRequest;

        //rtp and rtcp ports
        internal int m_Receieved, m_Sent;
        
        //Buffer for data
        internal byte[] m_Buffer = new byte[RtspMessage.MaximumLength];

        //Sockets
        internal Socket m_RtspSocket;

        //RtpClient
        internal RtpClient m_RtpClient;

        #endregion

        #region Properties

        public Guid Id { get { return m_Id; } }

        public string SessionId { get { return m_SessionId; } internal set { m_SessionId = value; } }

        public RtspRequest LastRequest { get { return m_LastRequest; } internal set { m_LastRequest = value; } }

        public Media.Sdp.SessionDescription SessionDescription { get { return m_SessionDescription; } internal set { m_SessionDescription = value; } }

        #endregion

        #region Constructor

        public RtspSession(RtspServer server, Socket rtspSocket)
        {
            m_Server = server;
            m_RtspSocket = rtspSocket;
        }

        ~RtspSession()
        {
            Disconnect();
        }

        #endregion

        #region Methods

        /// <summary>
        /// Sets up the Ssrc and Transport Sockets and starts the worker thread.
        /// </summary>
        /// <param name="rtpPort">The clients rtpPort</param>
        /// <param name="rtcpPort">The clients rtcpPort</param>
        internal void InitializeRtp(int rtpPort, int rtcpPort)
        {
            if (m_RtpClient != null) return;
            m_RtpClient = RtpClient.Sender(((IPEndPoint)m_RtspSocket.RemoteEndPoint).Address, rtpPort, rtcpPort);
            m_RtpClient.Connect();
        }

        /// <summary>
        /// Called for each RtpPacket received in the source RtpClient
        /// </summary>
        /// <param name="client">The RtpClient from which the packet arrived</param>
        /// <param name="packet">The packet which arrived</param>
        internal void OnSourceRtpPacketRecieved(RtpClient client, RtpPacket packet)
        {
            //Not required since this is a sender
            //Raise the event to allow Rtcp to be calulcated properly
            m_RtpClient.OnRtpPacketReceieved(packet);            

            //Enque the recieved packet
            m_RtpClient.EnquePacket(packet);     
        }

        /// <summary>
        /// Called for each RtcpPacket recevied in the source RtpClient
        /// </summary>
        /// <param name="stream">The listener from which the packet arrived</param>
        /// <param name="packet">The packet which arrived</param>
        internal void OnSourceRtcpPacketRecieved(RtpClient stream, RtcpPacket packet)
        {
            //SendRtcpPacket(packet);
            
            //E.g. when Stream Location changes on the fly ... could maybe also have events for started and stopped on the listener?
            if (packet.PacketType == RtcpPacket.RtcpPacketType.Goodbye) Disconnect();

            //maybe their recievers reports shoudl trigger us sending one to our client?
            //Maybe handle these but we dont need them we only need to be aware of packets from the client, not the source.
        }

        /// <summary>
        /// Sends the Rtcp Goodbye and disposes the Rtp and Rtcp Sockets if we are not in Tcp Transport
        /// </summary>
        internal void Disconnect()
        {
            if (m_RtpClient != null) m_RtpClient.Disconnect();
        }

        /// <summary>
        /// Creates a RtspResponse based on the SequenceNumber contained in the given RtspRequest
        /// </summary>
        /// <param name="request">The request to utilize the SequenceNumber from, if null the current SequenceNumber is used</param>
        /// <param name="statusCode">The StatusCode of the generated response</param>
        /// <returns>The RtspResponse created</returns>
        internal RtspResponse CreateRtspResponse(RtspRequest request = null, RtspResponse.ResponseStatusCode statusCode = RtspResponse.ResponseStatusCode.OK)
        {
            RtspResponse response = new RtspResponse();
            response.StatusCode = statusCode;
            response.CSeq = request != null ? request.CSeq : m_LastRequest.CSeq;
            if (!string.IsNullOrWhiteSpace(m_SessionId))
            {
                response.SetHeader(Rtsp.RtspMessage.RtspHeaders.Session, m_SessionId);
            }
            return response;
        }

        internal void CreateSessionDescription(RtspStream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");

            //Get the original so we can rewrite it
            Media.Sdp.SessionDescription origional = stream.SessionDescription;

            //http://www.ietf.org/rfc/rfc2327.txt - Page 8 the "o=" field.
            //Might choose to move this stuff to the SessionDescription Constructor

            //Should be 2 NTP Timestamps
            ulong[] parts = RtpClient.DateTimeToNptTimestamp(DateTime.Now.ToUniversalTime());
            string sessionId = parts[0].ToString(),
                sessionVersion = parts[1].ToString();

            //((IPEndPoint)stream.Client.Client.m_RtcpSocket.LocalEndPoint).Address

            //string originatorString = "- " + sessionId + " " + sessionVersion + " IN IP4 " + Utility.GetV4IPAddress().ToString();

            // stream.Client.Client.m_RtcpSocket should be the stream.Client.m_RtspSocket
            string originatorString = "- " + sessionId + " " + sessionVersion + " IN IP4 " + ((IPEndPoint)m_RtspSocket.LocalEndPoint).Address.ToString();

            string sessionName = "ASTI Streaming Session"; // + stream.Name 

            //Make the new SessionDescription
            Media.Sdp.SessionDescription result = new Sdp.SessionDescription(origional.Version, originatorString, sessionName);

            //Copy the old one
            origional.CopyTo(result);

            //If we were given a session than update it with the id, version and SessionDescription
            SessionId = sessionId;
            m_SDPVersion = sessionVersion;
            m_SessionDescription = result;
        }

        #endregion        
    }
}