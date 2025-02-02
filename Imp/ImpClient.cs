﻿using DouglasDwyer.Imp;
using DouglasDwyer.Imp.Messages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

[assembly: ShareAs(typeof(ImpClient<>), typeof(IImpClient<>))]
[assembly: ShareAs(typeof(ImpClient), typeof(IImpClient))]
namespace DouglasDwyer.Imp
{
    /// <summary>
    /// Represents a TCP client that can send shared interfaces across the network as references.
    /// </summary>
    [Shared]
    public class ImpClient : IImpClient
    {
        /// <summary>
        /// The remote server object, or local server object if this is a server-owned client.
        /// </summary>
        [Local]
        public IImpServer Server { get; private set; }
        /// <summary>
        /// The serializer used to send objects across the network.
        /// </summary>
        [Local]
        public INetworkSerializer Serializer { get; set; }
        /// <summary>
        /// The binder used to generate remote interfaces for shared objects.
        /// </summary>
        [Local]
        public IProxyBinder SharedTypeBinder { get; internal set; }
        /// <summary>
        /// Whether this object is a local, independent client or a server-owned object representing a connection to a remote host.
        /// </summary>
        [Local]
        public bool Local { get; }
        /// <summary>
        /// Whether this object has an active connection to a remote host.
        /// </summary>
        [Local]
        public bool Connected { get; private set; } = false;
        /// <summary>
        /// The unique network ID of this client, used to identify this client from others connected to a <see cref="ImpServer"/>.
        /// </summary>
        [Local]
        public ushort NetworkID { get; private set; }
        /// <summary>
        /// Controls the scheduling of remote method/accessor calls. By default, this scheduler is created with the <see cref="SynchronizationContext"/> of the thread that creates the client.
        /// </summary>
        [Local]
        public TaskScheduler RemoteTaskScheduler { get; set; }
        /// <summary>
        /// The maximum number of shared objects that this client will hold for the remote host. If the number of objects sent exceeds this threshold, the connection will be terminated.
        /// </summary>
        [Local]
        public int MaximumHeldObjects { get; set; } = int.MaxValue;
        /// <summary>
        /// The maximum number of remote shared interfaces that this client will hold. If the number of objects received exceeds this threshold, the connection will be terminated.
        /// </summary>
        [Local]
        public int MaximumRemoteObjects { get; set; } = int.MaxValue;

        internal IImpClient RemoteClient { get; private set; }

        private IdentifiedCollection<object> HeldObjects = new IdentifiedCollection<object>();
        private ConcurrentDictionary<ushort, CountedObject<object>> HeldObjectsData = new ConcurrentDictionary<ushort, CountedObject<object>>();
        private IdentifiedCollection<AsynchronousNetworkOperation> CurrentNetworkOperations = new IdentifiedCollection<AsynchronousNetworkOperation>();
        private ConcurrentDictionary<ushort, CountedObject<WeakReference<RemoteSharedObject>>> RemoteSharedObjects = new ConcurrentDictionary<ushort, CountedObject<WeakReference<RemoteSharedObject>>>();
        private Dictionary<Type, MethodInfo> MessageCallbacks = new Dictionary<Type, MethodInfo>();
        private TcpClient InternalClient;
        private UdpClient UnreliableClient;
        private IPEndPoint UnreliableRemoteEndPoint;
        private BinaryWriter MessageWriter;
        private Thread ListenerThread;
        private object Locker = new object();

        /// <summary>
        /// Creates a new <see cref="ImpClient"/>.
        /// </summary>
        public ImpClient() {
            LoadMethodCallbacks();
            Serializer = new ImpPowerSerializer(this);
            SharedTypeBinder = RuntimeProxyBinder.CreateAndBind();
            if (SynchronizationContext.Current is null)
            {
                RemoteTaskScheduler = TaskScheduler.Current;
            }
            else
            {
                RemoteTaskScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            }
            Local = true;
        }

        /// <summary>
        /// Creates a new server-owned <see cref="ImpClient"/>.
        /// </summary>
        /// <param name="client">The TCP connection this client is using to communicate.</param>
        /// <param name="server">The server that owns this client.</param>
        /// <param name="networkID">The network ID of this client.</param>
        internal ImpClient(TcpClient client, ImpServer server, ushort networkID, int maxHeldObjects, int maxRemoteObjects, UdpClient unreliableClient, IProxyBinder proxyBinder, ImpPowerSerializer serializer, TaskScheduler scheduler)
        {
            Connected = true;
            LoadMethodCallbacks();
            Serializer = serializer;
            Serializer.Client = this;
            RemoteTaskScheduler = scheduler;
            SharedTypeBinder = proxyBinder;
            MaximumHeldObjects = maxHeldObjects;
            MaximumRemoteObjects = maxRemoteObjects;
            Local = false;
            Server = server;
            NetworkID = networkID;
            InternalClient = client;
            InternalClient.NoDelay = true;
            MessageWriter = new BinaryWriter(InternalClient.GetStream());
            ListenerThread = new Thread(RunCommunications);
            ListenerThread.IsBackground = true;
            UnreliableClient = unreliableClient;

            InitializeServerConnection();

            ListenerThread.Start();
        }

        /// <summary>
        /// Attempts to connect to an ImpServer using the specified IP address and port number.
        /// </summary>
        /// <param name="ip">The address to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        [Local]
        public virtual void Connect(string ip, int port)
        {
            Connect(IPAddress.Parse(ip), port);
        }

        /// <summary>
        /// Attempts to connect to an ImpServer using the specified IP address and port number.
        /// </summary>
        /// <param name="ip">The address to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        [Local]
        public virtual void Connect(IPAddress ip, int port)
        {
            if (Connected || InternalClient != null)
            {
                throw new InvalidOperationException("The ImpClient is currently in use.");
            }
            lock (Locker)
            {
                InternalClient = new TcpClient();
            }
            InternalClient.NoDelay = true;
            InternalClient.Connect(ip, port);
            lock (Locker)
            {
                MessageWriter = new BinaryWriter(InternalClient.GetStream());
                ListenerThread = new Thread(RunCommunications);
                ListenerThread.IsBackground = true;
                BinaryReader networkReader = new BinaryReader(InternalClient.GetStream());

                InitializeClientConnection();

                Connected = true;
                ListenerThread.Start();
            }
        }

        /// <summary>
        /// Attempts to asynchronously connect to an ImpServer using the specified IP address and port number.
        /// </summary>
        /// <param name="ip">The address to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        /// <returns>A <see cref="Task"/> object representing the current state of the operation.</returns>
        [Local]
        public virtual Task ConnectAsync(string ip, int port)
        {
            if(ip == "localhost")
            {
                ip = "127.0.0.1";
            }
            return ConnectAsync(IPAddress.Parse(ip), port);
        }

        /// <summary>
        /// Attempts to asynchronously connect to an ImpServer using the specified IP address and port number.
        /// </summary>
        /// <param name="ip">The address to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        /// <returns>A <see cref="Task"/> object representing the current state of the operation.</returns>
        [Local]
        public virtual async Task ConnectAsync(IPAddress ip, int port)
        {
            if (Connected || InternalClient != null)
            {
                throw new InvalidOperationException("The ImpClient is currently in use.");
            }
            lock (Locker)
            {
                InternalClient = new TcpClient();
            }
            InternalClient.NoDelay = true;
            await InternalClient.ConnectAsync(ip, port);
            lock (Locker)
            {
                MessageWriter = new BinaryWriter(InternalClient.GetStream());
                ListenerThread = new Thread(RunCommunications);
                ListenerThread.IsBackground = true;
                BinaryReader networkReader = new BinaryReader(InternalClient.GetStream());

                InitializeClientConnection();

                Connected = true;
                ListenerThread.Start();
            }
        }

        private void InitializeClientConnection()
        {
            HeldObjectsData[HeldObjects.Add(this)] = new CountedObject<object>(this);
            BinaryReader reader = new BinaryReader(InternalClient.GetStream());
            NetworkID = reader.ReadUInt16();
            MessageWriter.Write(GetSharedInterfaceForType(GetType()).AssemblyQualifiedName);
            Server = (IImpServer)GetOrCreateRemoteSharedObject(0, Type.GetType(reader.ReadString()));

            IPEndPoint remoteEndPoint = (IPEndPoint)InternalClient.Client.RemoteEndPoint;
            UnreliableRemoteEndPoint = new IPEndPoint(remoteEndPoint.Address.AddressFamily == AddressFamily.InterNetwork ? remoteEndPoint.Address.MapToIPv6() : remoteEndPoint.Address, reader.ReadUInt16());
            Socket socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            UnreliableClient = new UdpClient(AddressFamily.InterNetworkV6);
            UnreliableClient.Client = socket;
            UnreliableClient.BeginReceive(ReceiveUnreliableData, null);
            MessageWriter.Write((ushort)((IPEndPoint)UnreliableClient.Client.LocalEndPoint).Port);
        }

        private void InitializeServerConnection()
        {
            MessageWriter.Write(NetworkID);
            HeldObjectsData[HeldObjects.Add(Server)] = new CountedObject<object>(Server);
            BinaryReader reader = new BinaryReader(InternalClient.GetStream());
            MessageWriter.Write(GetSharedInterfaceForType(Server.GetType()).AssemblyQualifiedName);
            MessageWriter.Write((ushort)((IPEndPoint)UnreliableClient.Client.LocalEndPoint).Port);
            RemoteClient = (IImpClient)GetOrCreateRemoteSharedObject(0, Type.GetType(reader.ReadString()));
            IPEndPoint remoteEndPoint = (IPEndPoint)InternalClient.Client.RemoteEndPoint;
            UnreliableRemoteEndPoint = new IPEndPoint(remoteEndPoint.Address.AddressFamily == AddressFamily.InterNetwork ? remoteEndPoint.Address.MapToIPv6() : remoteEndPoint.Address, reader.ReadUInt16());
        }

        private Type GetSharedInterfaceForType(Type type)
        {
            ShareAsAttribute attribute = type.GetCustomAttribute<ShareAsAttribute>();
            if(attribute is null)
            {
                return AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetCustomAttributes<ShareAsAttribute>()).Where(x => x.TypeToShare == type).Single().InterfaceBinding;
            }
            else
            {
                return attribute.InterfaceBinding;
            }
        }

        /// <summary>
        /// Disconnects from the remote host, ending communication between the server and the client.
        /// </summary>
        [Local]
        public virtual void Disconnect()
        {
            lock (Locker)
            {
                ProcessDisconnection();
                OnDisconnected();
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void CallRemoteUnreliableMethod(ushort obj, object[] arguments, Type[] genericArguments, ushort methodID)
        {
            SendUnreliableImpMessage(new CallRemoteUnreliableMethodMessage(obj, arguments, genericArguments, methodID));
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public T CallRemoteMethod<T>(ushort obj, object[] arguments, Type[] genericArguments, ushort methodID)
        {
            try
            {
                return CallRemoteMethodAsync<T>(obj, arguments, genericArguments, methodID).Result;
            }
            catch(AggregateException e)
            {
                if(e.InnerException is RemoteException x)
                {
                    throw x;
                }
                else
                {
                    throw;
                }
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public Task<T> CallRemoteMethodAsync<T>(ushort obj, object[] arguments, Type[] genericArguments, ushort methodID)
        {
            if (Connected)
            {
                AsynchronousNetworkOperation<T> operation = CreateNewAsynchronousNetworkOperation<T>();
                SendImpMessage(new CallRemoteMethodMessage(obj, methodID, arguments, genericArguments, operation.OperationID));
                return operation.Operation;
            }
            else
            {
                return Task.FromException<T>(new InvalidOperationException("An operation was attempted on an object owned by a disconnected host."));
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public T GetRemoteProperty<T>(ushort obj, ushort propertyID)
        {
            try {
                return GetRemotePropertyAsync<T>(obj, propertyID).Result;
            }
            catch (AggregateException e)
            {
                if (e.InnerException is RemoteException x)
                {
                    throw x;
                }
                else
                {
                    throw;
                }
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public Task<T> GetRemotePropertyAsync<T>(ushort obj, ushort propertyID)
        {
            if (Connected)
            {
                AsynchronousNetworkOperation<T> operation = CreateNewAsynchronousNetworkOperation<T>();
                SendImpMessage(new GetRemotePropertyMessage(obj, propertyID, operation.OperationID));
                return operation.Operation;
            }
            else
            {
                return Task.FromException<T>(new InvalidOperationException("An operation was attempted on an object owned by a disconnected host."));
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void SetRemoteProperty<T>(ushort obj, T toSet, ushort propertyID)
        {
            try {
                Task t = SetRemotePropertyAsync(obj, toSet, propertyID);
                t.Wait();
            }
            catch (AggregateException e)
            {
                if (e.InnerException is RemoteException x)
                {
                    throw x;
                }
                else
                {
                    throw;
                }
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public Task SetRemotePropertyAsync<T>(ushort obj, T toSet, ushort propertyID)
        {
            if (InternalClient.Connected)
            {
                AsynchronousNetworkOperation<object> operation = CreateNewAsynchronousNetworkOperation<object>();
                SendImpMessage(new SetRemotePropertyMessage(obj, propertyID, toSet, operation.OperationID));
                return operation.Operation;
            }
            else
            {
                return Task.FromException<T>(new InvalidOperationException("An operation was attempted on an object owned by a disconnected host."));
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public T GetRemoteIndexer<T>(ushort obj, object[] arguments, ushort propertyID)
        {
            try
            {
                return GetRemoteIndexerAsync<T>(obj, arguments, propertyID).Result;
            }
            catch (AggregateException e)
            {
                if (e.InnerException is RemoteException x)
                {
                    throw x;
                }
                else
                {
                    throw;
                }
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public Task<T> GetRemoteIndexerAsync<T>(ushort obj, object[] arguments, ushort propertyID)
        {
            if (InternalClient.Connected)
            {
                AsynchronousNetworkOperation<T> operation = CreateNewAsynchronousNetworkOperation<T>();
                SendImpMessage(new GetRemoteIndexerMessage(obj, propertyID, arguments, operation.OperationID));
                return operation.Operation;
            }
            else
            {
                return Task.FromException<T>(new InvalidOperationException("An operation was attempted on an object owned by a disconnected host."));
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void SetRemoteIndexer<T>(ushort obj, T toSet, object[] arguments, ushort propertyID)
        {
            try
            {
                SetRemoteIndexerAsync(obj, toSet, arguments, propertyID).Wait();
            }
            catch (AggregateException e)
            {
                if (e.InnerException is RemoteException x)
                {
                    throw x;
                }
                else
                {
                    throw;
                }
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public Task<object> SetRemoteIndexerAsync<T>(ushort obj, T toSet, object[] arguments, ushort propertyID)
        {
            if (InternalClient.Connected)
            {
                AsynchronousNetworkOperation<object> operation = CreateNewAsynchronousNetworkOperation<object>();
                SendImpMessage(new SetRemoteIndexerMessage(obj, propertyID, toSet, arguments, operation.OperationID));
                return operation.Operation;
            }
            else
            {
                return Task.FromException<object>(new InvalidOperationException("An operation was attempted on an object owned by a disconnected host."));
            }
        }

        private void SendImpMessage(object message)
        {
            byte[] toSend = Serializer.Serialize(message);
            Task.Run(() =>
            {
                lock (MessageWriter)
                {
                    MessageWriter.Write(toSend.Length);
                    MessageWriter.Write(toSend);
                }
            });
        }

        /// <summary>
        /// Called whenever this client is disconnected from the server due to an exception in networking code.
        /// </summary>
        /// <param name="exception">The exception that was thrown.</param>
        protected virtual void OnNetworkError(Exception exception)
        {
            if (!Local)
            {
                ((ImpServer)Server).ReportClientNetworkError(RemoteClient, exception);
            }
        }

        /// <summary>
        /// Called whenever this client disconnects from the server.
        /// </summary>
        protected virtual void OnDisconnected()
        {
            if(!Local)
            {
                ((ImpServer)Server).ReportClientDisconnected(RemoteClient);
            }
        }

        private void SendUnreliableImpMessage(object message)
        {
            if (Local)
            {
                IEnumerable<byte> netID = BitConverter.GetBytes(NetworkID);
                if (!BitConverter.IsLittleEndian) { netID = netID.Reverse(); }
                byte[] toSend = netID.Concat(Serializer.Serialize(message)).ToArray();
                UnreliableClient.Send(toSend, toSend.Length, UnreliableRemoteEndPoint);
            }
            else
            {
                byte[] toSend = Serializer.Serialize(message);
                UnreliableClient.Send(toSend, toSend.Length, UnreliableRemoteEndPoint);
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public RemoteSharedObject GetOrCreateRemoteSharedObject(ushort id, Type type)
        {
            lock(Locker)
            {
                if (RemoteSharedObjects.ContainsKey(id))
                {
                    CountedObject<WeakReference<RemoteSharedObject>> reference = RemoteSharedObjects[id];
                    RemoteSharedObject obj;
                    if (reference.ReferencedObject.TryGetTarget(out obj))
                    {
                        reference++;
                        return obj;
                    }
                    else
                    {
                        obj = (RemoteSharedObject)Activator.CreateInstance(SharedTypeBinder.GetRemoteType(type), id, this);
                        RemoteSharedObjects[id] = new CountedObject<WeakReference<RemoteSharedObject>>(new WeakReference<RemoteSharedObject>(obj));
                        return obj;
                    }
                }
                else
                {
                    if(RemoteSharedObjects.Count >= MaximumRemoteObjects)
                    {
                        throw new InvalidOperationException("The number of remote shared objects has exceeded the maximum allowed.");
                    }
                    RemoteSharedObject obj = (RemoteSharedObject)Activator.CreateInstance(SharedTypeBinder.GetRemoteType(type), id, this);
                    RemoteSharedObjects[id] = new CountedObject<WeakReference<RemoteSharedObject>>(new WeakReference<RemoteSharedObject>(obj));
                    return obj;
                }
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public ushort GetOrRegisterLocalSharedObject(object obj)
        {
            lock(Locker)
            {
                if(HeldObjects.ContainsValue(obj))
                {
                    ushort path = HeldObjects[obj];
                    HeldObjectsData[path]++;
                    return path;
                }
                else
                {
                    if(HeldObjects.Count >= MaximumHeldObjects)
                    {
                        throw new InvalidOperationException("The number of held shared objects has exceeded the maximum allowed.");
                    }
                    ushort path = HeldObjects.Add(obj);
                    HeldObjectsData[path] = new CountedObject<object>(obj);
                    return path;
                }
            }
        }

        [Local]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public object RetrieveLocalSharedObject(ushort id)
        {
            lock(Locker)
            {
                return HeldObjects[id];
            }
        }

        internal void ReleaseRemoteSharedObject(ushort path)
        {
            CountedObject<WeakReference<RemoteSharedObject>> sharedObject;
            if (RemoteSharedObjects.TryRemove(path, out sharedObject))
            {
                SendImpMessage(new RemoteSharedObjectReleasedMessage(sharedObject.Count, path));
            }
        }

        private AsynchronousNetworkOperation<T> CreateNewAsynchronousNetworkOperation<T>()
        {
            AsynchronousNetworkOperation<T> toReturn = null;
            CurrentNetworkOperations.Add(x => { return toReturn = new AsynchronousNetworkOperation<T>(x, y => CurrentNetworkOperations.Remove(x)); });
            return toReturn;
        }

        [MessageCallback]
        private void RemoteSharedObjectReleasedCallback(RemoteSharedObjectReleasedMessage message)
        {
            lock(Locker)
            {
                CountedObject<object> obj = HeldObjectsData[message.ObjectID];
                if (obj.Count > message.Count)
                {
                    obj.SetCount(obj.Count - message.Count);
                }
                else
                {
                    HeldObjects.Remove(message.ObjectID);
                    HeldObjectsData.TryRemove(message.ObjectID, out _);
                }
            }
        }

        [MessageCallback]
        private void CallRemoteUnreliableMethodCallback(CallRemoteUnreliableMethodMessage message)
        {
            object toInvoke = HeldObjects[message.ObjectID];
            SharedTypeBinder.GetDataForSharedType(toInvoke.GetType()).Methods[message.MethodID].Invoke(Local ? this : RemoteClient, this, toInvoke, message.Parameters, message.GenericArguments);
        }

       /* [MessageCallback]
        private void GetRemoteServerObjectCallback(GetRemoteServerObjectMessage message)
        {
            SendImpMessage(new ReturnRemoteServerObjectMessage(Server));
        }

        [MessageCallback]
        private void ReturnRemoteServerObjectCallback(ReturnRemoteServerObjectMessage message)
        {
            if(Local && RemoteServer is null)
            {
                RemoteServer = (IImpServer)message.Server;
            }
        }*/

        [MessageCallback]
        private async Task CallRemoteMethodCallbackAsync(CallRemoteMethodMessage message)
        {
            object toInvoke;
            lock (Locker)
            {
                if (HeldObjects.ContainsID(message.InvocationTarget))
                {
                    toInvoke = HeldObjects[message.InvocationTarget];
                }
                else
                {
                    SendImpMessage(new ReturnRemoteMethodMessage(message.OperationID, null, new RemoteException("Remote endpoint attempted to access remote object that it does not hold.", Environment.StackTrace)));
                    return;
                }
            }
            try
            {
                SendImpMessage(new ReturnRemoteMethodMessage(message.OperationID, await SharedTypeBinder.GetDataForSharedType(toInvoke.GetType()).Methods[message.MethodID].Invoke(Local ? this : RemoteClient, this, toInvoke, message.Arguments, message.GenericArguments), null));
            }
            catch (Exception e)
            {
                SendImpMessage(new ReturnRemoteMethodMessage(message.OperationID, null, new RemoteException(e.GetType().FullName + ": " + e.Message, e.StackTrace, e.Source)));
            }
        }

        [MessageCallback]
        private void ReturnRemoteMethodCallback(ReturnRemoteMethodMessage message)
        {
            if (message.ExceptionResult is null)
            {
                CurrentNetworkOperations[message.OperatonID].SetResult(message.Result, RemoteTaskScheduler);
            }
            else
            {
                CurrentNetworkOperations[message.OperatonID].SetException(message.ExceptionResult, RemoteTaskScheduler);
            }
        }

        [MessageCallback]
        private async Task GetRemotePropertyCallbackAsync(GetRemotePropertyMessage message)
        {
            object toInvoke;
            lock (Locker)
            {
                if(HeldObjects.ContainsID(message.InvocationTarget))
                {
                    toInvoke = HeldObjects[message.InvocationTarget];
                }
                else
                {
                    SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, new RemoteException("Remote endpoint attempted to access remote object that it does not hold.", Environment.StackTrace)));
                    return;
                }
            }
            object returnValue = null;
            try
            {
                returnValue = await Task.Factory.StartNew(() => SharedTypeBinder.GetDataForSharedType(toInvoke.GetType()).Properties[message.PropertyID].GetValue(toInvoke), CancellationToken.None, TaskCreationOptions.None, RemoteTaskScheduler);
            }
            catch (Exception e)
            {
                SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, new RemoteException(e.Message, e.StackTrace, e.Source)));
                return;
            }
            SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, returnValue, null));
        }

        [MessageCallback]
        private void ReturnRemotePropertyCallback(ReturnRemotePropertyMessage message)
        {
            if (message.ExceptionResult is null)
            {
                CurrentNetworkOperations[message.OperatonID].SetResult(message.Result, RemoteTaskScheduler);
            }
            else
            {
                CurrentNetworkOperations[message.OperatonID].SetException(message.ExceptionResult, RemoteTaskScheduler);
            }
        }

        [MessageCallback]
        private async Task SetRemotePropertyCallbackAsync(SetRemotePropertyMessage message)
        {
            object toInvoke;
            lock (Locker)
            {
                if (HeldObjects.ContainsID(message.InvocationTarget))
                {
                    toInvoke = HeldObjects[message.InvocationTarget];
                }
                else
                {
                    SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, new RemoteException("Remote endpoint attempted to access remote object that it does not hold.", Environment.StackTrace)));
                    return;
                }
            }
            try
            {
                await Task.Factory.StartNew(() => SharedTypeBinder.GetDataForSharedType(toInvoke.GetType()).Properties[message.PropertyID].SetValue(toInvoke, message.Value), CancellationToken.None, TaskCreationOptions.None, RemoteTaskScheduler);
                SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, null));
            }
            catch (Exception e)
            {
                SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, new RemoteException(e.Message, e.StackTrace, e.Source)));
            }
        }

        [MessageCallback]
        private async Task GetRemoteIndexerCallbackAsync(GetRemoteIndexerMessage message)
        {
            object toInvoke;
            lock (Locker)
            {
                if (HeldObjects.ContainsID(message.InvocationTarget))
                {
                    toInvoke = HeldObjects[message.InvocationTarget];
                }
                else
                {
                    SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, new RemoteException("Remote endpoint attempted to access remote object that it does not hold.", Environment.StackTrace)));
                    return;
                }
            }
            try
            {
                object result = await Task.Factory.StartNew(() => SharedTypeBinder.GetDataForSharedType(toInvoke.GetType()).Properties[message.PropertyID].GetValue(toInvoke, message.Parameters), CancellationToken.None, TaskCreationOptions.None, RemoteTaskScheduler);
                SendImpMessage(new ReturnRemoteIndexerMessage(message.OperationID, result, null));
            }
            catch (Exception e)
            {
                SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, new RemoteException(e.Message, e.StackTrace, e.Source)));
            }
        }

        [MessageCallback]
        private async Task SetRemoteIndexerCallbackAsync(SetRemoteIndexerMessage message)
        {
            object toInvoke;
            lock (Locker)
            {
                if (HeldObjects.ContainsID(message.InvocationTarget))
                {
                    toInvoke = HeldObjects[message.InvocationTarget];
                }
                else
                {
                    SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, new RemoteException("Remote endpoint attempted to access remote object that it does not hold.", Environment.StackTrace)));
                    return;
                }
            }
            try
            {
                await Task.Factory.StartNew(() => SharedTypeBinder.GetDataForSharedType(toInvoke.GetType()).Properties[message.PropertyID].SetValue(toInvoke, message.Value, message.Arguments), CancellationToken.None, TaskCreationOptions.None, RemoteTaskScheduler);
                SendImpMessage(new ReturnRemoteIndexerMessage(message.OperationID, null, null));
            }
            catch (Exception e)
            {
                SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, new RemoteException(e.Message, e.StackTrace, e.Source)));
            }
        }

        [MessageCallback]
        private void ReturnRemoteIndexerCallback(ReturnRemoteIndexerMessage message)
        {
            if (message.ExceptionResult is null)
            {
                CurrentNetworkOperations[message.OperatonID].SetResult(message.Result, RemoteTaskScheduler);
            }
            else
            {
                CurrentNetworkOperations[message.OperatonID].SetException(message.ExceptionResult, RemoteTaskScheduler);
            }
        }

        internal bool DoesRemoteEndPointMatch(IPEndPoint endPoint)
        {
            return endPoint.Equals(UnreliableRemoteEndPoint);
        }

        internal void ProcessUnreliableMessage(byte[] message)
        {
            object o = Serializer.Deserialize<ImpMessage>(message);
            lock (Locker)
            {
                MessageCallbacks[o.GetType()].Invoke(this, new[] { o });
            }
        }

        private void ReceiveUnreliableData(IAsyncResult result)
        {
            try
            {
                IPEndPoint point = null;
                byte[] b = UnreliableClient.EndReceive(result, ref point);
                UnreliableClient.BeginReceive(ReceiveUnreliableData, null);
                if (point.Equals(UnreliableRemoteEndPoint))
                {
                    ProcessUnreliableMessage(b);
                }
            }
            catch (Exception e)
            {
                lock (Locker)
                {
                    bool wasConnected = Connected;
                    ProcessDisconnection();
                    if (wasConnected)
                    {
                        OnNetworkError(e);
                        OnDisconnected();
                    }
                }
            }
        }

        private void RunCommunications()
        {
            try
            {
                NetworkStream str = InternalClient.GetStream();
                BinaryReader networkReader = new BinaryReader(InternalClient.GetStream());
                while(true)
                {
                    int messageLength = networkReader.ReadInt32();
                    object o = Serializer.Deserialize<ImpMessage>(networkReader.ReadBytes(messageLength));
                    MessageCallbacks[o.GetType()].Invoke(this, new[] { o });
                }
            }
            catch(Exception e)
            {
                lock(Locker)
                {
                    bool wasConnected = Connected;
                    ProcessDisconnection();
                    if(wasConnected)
                    {
                        if(!(e is EndOfStreamException) && !(e is IOException && (e.Message == "An existing connection was forcibly closed by the remote host." || e.Message == "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host..")))
                        {
                            OnNetworkError(e);
                        }
                        OnDisconnected();
                    }
                }
            }
        }

        private void ProcessDisconnection()
        {
            lock (Locker)
            {
                try
                {
                    InternalClient.Close();
                }
                catch { }
                try
                {
                    if (Local)
                    {
                        UnreliableClient.Close();
                    }
                }
                catch { }

                foreach(AsynchronousNetworkOperation operation in CurrentNetworkOperations.Values)
                {
                    operation.SetException(new IOException("The client was disconnected while processing this network operation."), RemoteTaskScheduler);
                }
                foreach(CountedObject<WeakReference<RemoteSharedObject>> rem in RemoteSharedObjects.Values)
                {
                    RemoteSharedObject obj;
                    if(rem.ReferencedObject.TryGetTarget(out obj))
                    {
                        obj.HostClient = null;
                    }
                }
                CurrentNetworkOperations = new IdentifiedCollection<AsynchronousNetworkOperation>();
                HeldObjects = new IdentifiedCollection<object>();
                HeldObjectsData.Clear();
                RemoteSharedObjects.Clear();

                Connected = false;
                InternalClient = null;
            }
        }

        private void LoadMethodCallbacks()
        {
            foreach (MethodInfo info in typeof(ImpClient).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if(info.GetCustomAttribute<MessageCallbackAttribute>() != null)
                {
                    MessageCallbacks[info.GetParameters()[0].ParameterType] = info;
                }
            }
        }
    }

    /// <summary>
    /// Represents a TCP client that can send shared interfaces across the network as references.
    /// </summary>
    /// <typeparam name="T">The shared interface type of the server to which this client will connect.</typeparam>
    public class ImpClient<T> : ImpClient, IImpClient<T> where T : IImpServer
    {
        /// <summary>
        /// The remote server object, or local server object if this is a server-owned client.
        /// </summary>
        [Local]
        public new T Server => (T)base.Server;
    }
}