﻿using Kaenx.Konnect.Addresses;
using Kaenx.Konnect.Builders;
using Kaenx.Konnect.Classes;
using Kaenx.Konnect.Messages;
using Kaenx.Konnect.Messages.Request;
using Kaenx.Konnect.Messages.Response;
using Kaenx.Konnect.Parser;
using Kaenx.Konnect.Responses;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Kaenx.Konnect.Connections.IKnxConnection;

namespace Kaenx.Konnect.Connections
{
    public class KnxIpRouting : IKnxConnection
    {
        public event TunnelRequestHandler OnTunnelRequest;
        public event TunnelResponseHandler OnTunnelResponse;
        public event TunnelAckHandler OnTunnelAck;
        public event SearchResponseHandler OnSearchResponse;
        public event ConnectionChangedHandler ConnectionChanged;

        public int Port;
        public bool IsConnected { get; set; }
        public ConnectionErrors LastError { get; set; }
        public UnicastAddress PhysicalAddress { get; set; }

        private ProtocolTypes CurrentType { get; set; } = ProtocolTypes.cEmi;
        private byte _communicationChannel;
        private bool StopProcessing = false;
        private byte _sequenceCounter = 0;

        private readonly IPEndPoint _receiveEndPoint;
        private readonly IPEndPoint _sendEndPoint;
        private List<UdpClient> _udpList = new List<UdpClient>();
        private UdpClient _udp;
        private readonly BlockingCollection<object> _sendMessages;
        private readonly ReceiverParserDispatcher _receiveParserDispatcher;
        private bool _flagCRRecieved = false;

        public KnxIpRouting()
        {
            Port = GetFreePort();
            _sendEndPoint = new IPEndPoint(IPAddress.Parse("224.0.23.12"), 3671);

            //_receiveEndPoint = new IPEndPoint(IP, Port);
            _receiveParserDispatcher = new ReceiverParserDispatcher();
            _sendMessages = new BlockingCollection<object>();

            Init();
        }

        private void Init()
        {
            //_udp = new UdpClient(new IPEndPoint(IPAddress.Parse("192.168.178.221"), 8088));
            ////_udp.JoinMulticastGroup(IPAddress.Parse("224.100.0.1"), 50);
            //_udp.JoinMulticastGroup(IPAddress.Parse("224.100.0.1"), IPAddress.Parse("192.168.178.221"));
            //_udp.MulticastLoopback = true;
            //_udp.Client.MulticastLoopback = true;
            //ProcessReceivingMessages(_udp);
            //ProcessSendMessages();


            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            int port = 8088;

            foreach (NetworkInterface adapter in nics)
            {

                try
                {
                    IPInterfaceProperties ipprops = adapter.GetIPProperties();
                    if (ipprops.MulticastAddresses.Count == 0 // most of VPN adapters will be skipped
                        || !adapter.SupportsMulticast // multicast is meaningless for this type of connection
                        || OperationalStatus.Up != adapter.OperationalStatus) // this adapter is off or not connected
                        continue;
                    IPv4InterfaceProperties p = ipprops.GetIPv4Properties();
                    if (null == p) continue; // IPv4 is not configured on this adapter
                    int index = IPAddress.HostToNetworkOrder(p.Index);

                    IPAddress addr = adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Single().Address;
                    UdpClient _udpClient = new UdpClient();
                    _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _udpClient.Client.Bind(new IPEndPoint(addr, port));
                    _udpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, index);
                    //_udpClient.JoinMulticastGroup(IPAddress.Parse("224.0.3.12"), addr);
                    _udpClient.MulticastLoopback = true;
                    _udpClient.Client.MulticastLoopback = true;
                    //_udpClient.BeginReceive(test, null);
                    _udpList.Add(_udpClient);

                    Debug.WriteLine("Binded to " + adapter.Name + " - " + addr.ToString() + " - " + port++);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Exception binding to " + adapter.Name);
                    Debug.WriteLine(ex.Message);
                }
            }


            ProcessSendMessages();

            foreach (UdpClient client in _udpList)
                ProcessReceivingMessages(client);

            IsConnected = true;
        }

        public static int GetFreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }


        public Task Send(byte[] data, byte sequence)
        {
            List<byte> xdata = new List<byte>();

            //KNX/IP Header
            xdata.Add(0x06); //Header Length
            xdata.Add(0x10); //Protokoll Version 1.0
            xdata.Add(0x04); //Service Identifier Family: Tunneling
            xdata.Add(0x20); //Service Identifier Type: Request
            xdata.AddRange(BitConverter.GetBytes(Convert.ToInt16(data.Length + 10)).Reverse()); //Total length. Set later

            //Connection header
            xdata.Add(0x04); // Body Structure Length
            xdata.Add(_communicationChannel); // Channel Id
            xdata.Add(sequence); // Sequenz Counter
            xdata.Add(0x00); // Reserved
            xdata.AddRange(data);

            _sendMessages.Add(xdata.ToArray());

            return Task.CompletedTask;
        }


        public Task Send(byte[] data, bool ignoreConnected = false)
        {
            if (!ignoreConnected && !IsConnected)
                throw new Exception("Roflkopter 1");

            _sendMessages.Add(data);

            return Task.CompletedTask;
        }

        public Task<byte> Send(IMessage message, bool ignoreConnected = false)
        {
            if (!ignoreConnected && !IsConnected)
                throw new Exception("Roflkopter 2");

            byte seq = _sequenceCounter++;
            message.SequenceCounter = seq;
            _sendMessages.Add(message);

            return Task.FromResult(seq);
        }

        public void Search() {
            Send(new MsgSearchReq());
        }

        public async Task Connect()
        {
            //Nothing to do here
        }

        public async Task Disconnect()
        {
            //Nothing to do here
        }

        public async Task<bool> SendStatusReq()
        {
            //TODO check if its needed
            //ConnectionStatusRequest stat = new ConnectionStatusRequest();
            //stat.Build(_receiveEndPoint, _communicationChannel);
            //stat.SetChannelId(_communicationChannel);
            //await Send(stat.GetBytes());
            //await Task.Delay(200);
            //return IsConnected;
            return true;
        }


        private void ProcessReceivingMessages(UdpClient _udpClient)
        {
            Debug.WriteLine("Höre jetzt auf: " + (_udpClient.Client.LocalEndPoint as IPEndPoint).Port);
            Task.Run(async () =>
            {
                int rofl = 0;
                try
                {

                    while (!StopProcessing)
                    {
                        rofl++;
                        var result = await _udpClient.ReceiveAsync();
                        var knxResponse = _receiveParserDispatcher.Build(result.Buffer);

                        switch (knxResponse)
                        {
                            case ConnectStateResponse connectStateResponse:
                                Debug.WriteLine("Connection State Response: " + connectStateResponse.Status.ToString());
                                switch (connectStateResponse.Status)
                                {
                                    case 0x00:
                                        IsConnected = true;
                                        ConnectionChanged?.Invoke(IsConnected);
                                        break;
                                    default:
                                        Debug.WriteLine("Connection State: Fehler: " + connectStateResponse.Status.ToString());
                                        LastError = ConnectionErrors.NotConnectedToBus;
                                        IsConnected = false;
                                        ConnectionChanged?.Invoke(IsConnected);
                                        break;
                                }
                                break;

                            case ConnectResponse connectResponse:
                                _flagCRRecieved = true;
                                switch (connectResponse.Status)
                                {
                                    case 0x00:
                                        _sequenceCounter = 0;
                                        _communicationChannel = connectResponse.CommunicationChannel;
                                        IsConnected = true;
                                        ConnectionChanged?.Invoke(IsConnected);
                                        PhysicalAddress = connectResponse.ConnectionResponseDataBlock.KnxAddress;
                                        Debug.WriteLine("Connected: Eigene Adresse: " + PhysicalAddress.ToString());
                                        break;
                                    default:
                                        Debug.WriteLine("Connected: Fehler: " + connectResponse.Status.ToString());
                                        LastError = ConnectionErrors.Undefined;
                                        IsConnected = false;
                                        ConnectionChanged?.Invoke(IsConnected);
                                        break;
                                }
                                break;

                            case Builders.TunnelResponse tunnelResponse:
                                if (tunnelResponse.IsRequest && tunnelResponse.DestinationAddress != PhysicalAddress)
                                {
                                    Debug.WriteLine("Telegram erhalten das nicht mit der Adresse selbst zu tun hat!");
                                    Debug.WriteLine("Typ: " + tunnelResponse.APCI);
                                    Debug.WriteLine("Eigene Adresse: " + PhysicalAddress.ToString());
                                    break;
                                }

                                _sendMessages.Add(new Responses.TunnelResponse(0x06, 0x10, 0x0A, 0x04, _communicationChannel, tunnelResponse.SequenceCounter, 0x00).GetBytes());

                                //Debug.WriteLine("Telegram APCI: " + tunnelResponse.APCI.ToString());

                                if (tunnelResponse.APCI.ToString().EndsWith("Response"))
                                {
                                    List<byte> data = new List<byte>() { 0x11, 0x00 };
                                    TunnelRequest builder = new TunnelRequest();
                                    builder.Build(UnicastAddress.FromString("0.0.0"), tunnelResponse.SourceAddress, ApciTypes.Ack, tunnelResponse.SequenceNumber);
                                    data.AddRange(builder.GetBytes());
                                    _=Send(data.ToArray(), _sequenceCounter);
                                    _sequenceCounter++;
                                    //Debug.WriteLine("Got Response " + tunnelResponse.SequenceCounter + " . " + tunnelResponse.SequenceNumber);

                                    
                                }
                                else if (tunnelResponse.APCI == ApciTypes.Ack)
                                {
                                    OnTunnelAck?.Invoke(new MsgAckRes()
                                    {
                                        ChannelId = tunnelResponse.CommunicationChannel,
                                        SequenceCounter = tunnelResponse.SequenceCounter,
                                        SequenceNumber = tunnelResponse.SequenceNumber,
                                        SourceAddress = tunnelResponse.SourceAddress,
                                        DestinationAddress = tunnelResponse.DestinationAddress
                                    });
                                    break;
                                }


                                List<string> temp = new List<string>();
                                var q = from t in Assembly.GetExecutingAssembly().GetTypes()
                                        where t.IsClass && t.IsNested == false && (t.Namespace == "Kaenx.Konnect.Messages.Response" || t.Namespace == "Kaenx.Konnect.Messages.Request")
                                        select t;

                                IMessage message = null;

                                foreach (Type t in q.ToList())
                                {
                                    IMessage resp = (IMessage)Activator.CreateInstance(t);

                                    if (resp.ApciType == tunnelResponse.APCI)
                                    {
                                        message = resp;
                                        break;
                                    }
                                }


                                if (message == null)
                                {
                                    //throw new Exception("Kein MessageParser für den APCI " + tunnelResponse.APCI);
                                    message = new MsgDefaultRes()
                                    {
                                        ApciType = tunnelResponse.APCI
                                    };
                                    Debug.WriteLine("Kein MessageParser für den APCI " + tunnelResponse.APCI);
                                }

                                message.Raw = tunnelResponse.Data;
                                message.ChannelId = tunnelResponse.CommunicationChannel;
                                message.SequenceCounter = tunnelResponse.SequenceCounter;
                                message.SequenceNumber = tunnelResponse.SequenceNumber;
                                message.SourceAddress = tunnelResponse.SourceAddress;
                                message.DestinationAddress = tunnelResponse.DestinationAddress;

                                switch (CurrentType)
                                {
                                    case ProtocolTypes.cEmi:
                                        message.ParseDataCemi();
                                        break;
                                    case ProtocolTypes.Emi1:
                                        message.ParseDataEmi1();
                                        break;
                                    case ProtocolTypes.Emi2:
                                        message.ParseDataEmi2();
                                        break;
                                    default:
                                        throw new NotImplementedException("Unbekanntes Protokoll - TunnelResponse KnxIpTunneling");
                                }


                                if (tunnelResponse.APCI.ToString().EndsWith("Response"))
                                    OnTunnelResponse?.Invoke(message as IMessageResponse);
                                else
                                    OnTunnelRequest?.Invoke(message as IMessageRequest);

                                break;

                            case SearchResponse searchResponse:
                                MsgSearchRes msg = new MsgSearchRes(searchResponse.responseBytes);
                                switch(CurrentType)
                                {
                                    case ProtocolTypes.cEmi:
                                        msg.ParseDataCemi();
                                        break;
                                    case ProtocolTypes.Emi1:
                                        msg.ParseDataEmi1();
                                        break;
                                    case ProtocolTypes.Emi2:
                                        msg.ParseDataEmi2();
                                        break;
                                    default:
                                        throw new NotImplementedException("Unbekanntes Protokoll - SearchResponse KnxIpTunneling");
                                }
                                OnSearchResponse?.Invoke(msg);
                                break;

                            case TunnelAckResponse tunnelAck:
                                //Do nothing
                                break;

                            case DisconnectResponse disconnectResponse:
                                IsConnected = false;
                                _communicationChannel = 0;
                                ConnectionChanged?.Invoke(IsConnected);
                                break;
                        }
                    }

                    Debug.WriteLine("Stopped Processing Messages " + _udpClient.Client.LocalEndPoint.ToString());
                    _udpClient.Close();
                    _udpClient.Dispose();
                }
                catch
                {

                }
            });
        }

        private void ProcessSendMessages()
        {
            Task.Run(() =>
            {

                foreach (var sendMessage in _sendMessages.GetConsumingEnumerable())
                {
                    if (sendMessage is byte[])
                    {

                        byte[] data = sendMessage as byte[]; 
                        _udp.SendAsync(data, data.Length, _sendEndPoint);

                    }
                    else if (sendMessage is MsgSearchReq)
                    {
                        MsgSearchReq message = sendMessage as MsgSearchReq;

                        foreach(UdpClient _udp in _udpList)
                        {
                            message.Endpoint = new IPEndPoint(IPAddress.Parse("192.168.178.221"), (_udp.Client.LocalEndPoint as IPEndPoint).Port);
                            byte[] xdata;

                            switch (CurrentType)
                            {
                                case ProtocolTypes.Emi1:
                                    xdata = message.GetBytesEmi1();
                                    break;

                                case ProtocolTypes.Emi2:
                                    xdata = message.GetBytesEmi1(); //Todo check diffrences to emi1
                                                                    //xdata.AddRange(message.GetBytesEmi2());
                                    break;

                                case ProtocolTypes.cEmi:
                                    xdata = message.GetBytesCemi();
                                    break;

                                default:
                                    throw new Exception("Unbekanntes Protokoll");
                            }

                            _udp.SendAsync(xdata, xdata.Length, _sendEndPoint);
                        }
                    } else if(sendMessage is IMessage) { 
                        IMessage message = sendMessage as IMessage;
                        List<byte> xdata = new List<byte>();

                        //KNX/IP Header
                        xdata.Add(0x06); //Header Length
                        xdata.Add(0x10); //Protokoll Version 1.0
                        xdata.Add(0x04); //Service Identifier Family: Tunneling
                        xdata.Add(0x20); //Service Identifier Type: Request
                        xdata.AddRange(new byte[] { 0x00, 0x00 }); //Total length. Set later

                        //Connection header
                        xdata.Add(0x04); // Body Structure Length
                        xdata.Add(_communicationChannel); // Channel Id
                        xdata.Add(message.SequenceCounter); // Sequenz Counter
                        xdata.Add(0x00); // Reserved


                        switch (CurrentType)
                        {
                            case ProtocolTypes.Emi1:
                                xdata.AddRange(message.GetBytesEmi1());
                                break;

                            case ProtocolTypes.Emi2:
                                xdata.AddRange(message.GetBytesEmi1()); //Todo check diffrences between emi1
                                                                        //xdata.AddRange(message.GetBytesEmi2());
                                break;

                            case ProtocolTypes.cEmi:
                                xdata.AddRange(message.GetBytesCemi());
                                break;

                            default:
                                throw new Exception("Unbekanntes Protokoll");
                        }

                        byte[] length = BitConverter.GetBytes((ushort)(xdata.Count));
                        Array.Reverse(length);
                        xdata[4] = length[0];
                        xdata[5] = length[1];

                        _udp.SendAsync(xdata.ToArray(), xdata.Count, _sendEndPoint);
                    }
                    else
                    {
                        throw new Exception("Unbekanntes Element in SendQueue! " + sendMessage.GetType().FullName);
                    }
                }
            });
        }
    }
}
