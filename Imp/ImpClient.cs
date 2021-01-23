﻿using DouglasDwyer.Imp;
using DouglasDwyer.Imp.Messages;
using DouglasDwyer.Imp.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

[assembly: ShareAs(typeof(ImpClient<>), typeof(IImpClient))]
[assembly: ShareAs(typeof(ImpClient), typeof(IImpClient))]
namespace DouglasDwyer.Imp
{
    /// <summary>
    /// Represents a TCP client that can send <see cref="Shared"/> interfaces across the network as references.
    /// </summary>
    [Shared]
    public class ImpClient : IImpClient
    {
        /// <summary>
        /// The remote server object, or local server object if this is a server-owned client.
        /// </summary>
        public IImpServer Server
        {
            get
            {
                if(RemoteServer is null)
                {
                    SendImpMessage(new GetRemoteServerObjectMessage());
                    while(RemoteServer is null) { }
                }
                return RemoteServer;
            }
            private set
            {
                RemoteServer = value;
            }
        }

        public ImpSerializer Serializer { get; }

        public IProxyBinder SharedTypeBinder { get; internal set; }
        /// <summary>
        /// The remote server reference.
        /// </summary>
        private IImpServer RemoteServer;

        /// <summary>
        /// Whether this object is a local, independent client or a server-owned object representing a connection to a remote host.
        /// </summary>
        public bool Local { get; }
        /// <summary>
        /// The unique network ID of this client, used to identify this client from others connected to a <see cref="ImpServer"/>. This ID is always 0 for server-owned objects.
        /// </summary>
        public ushort NetworkID { get; private set; }
        /// <summary>
        /// Controls the scheduling of remote method/accessor calls. By default, this scheduler is created with the <see cref="SynchronizationContext"/> of the thread that creates the client.
        /// </summary>
        public TaskScheduler RemoteTaskScheduler { get; set; }

        /// <summary>
        /// An ID-indexed collection of the objects that the remote endpoint can reference.
        /// </summary>
        private IdentifiedCollection<object> HeldObjects = new IdentifiedCollection<object>();
        private ConcurrentDictionary<ushort, CountedObject<object>> HeldObjectsData = new ConcurrentDictionary<ushort, CountedObject<object>>();
        /// <summary>
        /// An ID-indexed collection of the current asynchronous operations being performed by the client, like invoking a remote method.
        /// </summary>
        private IdentifiedCollection<AsynchronousNetworkOperation> CurrentNetworkOperations = new IdentifiedCollection<AsynchronousNetworkOperation>();

        private ConcurrentDictionary<SharedObjectPath, CountedObject<WeakReference<RemoteSharedObject>>> RemoteSharedObjects = new ConcurrentDictionary<SharedObjectPath, CountedObject<WeakReference<RemoteSharedObject>>>();

        private Dictionary<Type, MethodInfo> MessageCallbacks = new Dictionary<Type, MethodInfo>();
        /// <summary>
        /// The underlying TCP connection this client uses to communicate with the remote host.
        /// </summary>
        private TcpClient InternalClient;
        private BinaryWriter MessageWriter;
        private Thread ListenerThread;

        /// <summary>
        /// Creates a new <see cref="ImpClient"/>.
        /// </summary>
        public ImpClient() {
            LoadMethodCallbacks();
            Serializer = new ImpClientSerializer(this);
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
        /*
        /// <summary>
        /// Creates a new server-owned <see cref="ImpClient"/>.
        /// </summary>
        /// <param name="client">The TCP connection this client is using to communicate.</param>
        /// <param name="server">The server that owns this client.</param>
        /// <param name="networkID">The network ID of this client.</param>
        internal ImpClient(TcpClient client, ImpServer server, ushort networkID, TaskScheduler scheduler)
        {
            LoadMethodCallbacks();
            Serializer = new ImpClientSerializer(this);
            RemoteTaskScheduler = scheduler;
            Local = false;
            Server = server;
            NetworkID = networkID;
            InternalClient = client;
            InternalClient.NoDelay = true;
            MessageWriter = new BinaryWriter(InternalClient.GetStream());
            ListenerThread = new Thread(RunCommunications);
            ListenerThread.IsBackground = true;
            ListenerThread.Start();
        }*/

        /// <summary>
        /// Creates a new server-owned <see cref="ImpClient"/>.
        /// </summary>
        /// <param name="client">The TCP connection this client is using to communicate.</param>
        /// <param name="server">The server that owns this client.</param>
        /// <param name="networkID">The network ID of this client.</param>
        internal ImpClient(TcpClient client, ImpServer server, ushort networkID, IProxyBinder proxyBinder, ImpSerializer serializer, TaskScheduler scheduler)
        {
            LoadMethodCallbacks();
            Serializer = serializer;
            RemoteTaskScheduler = scheduler;
            SharedTypeBinder = proxyBinder;
            Local = false;
            Server = server;
            NetworkID = networkID;
            InternalClient = client;
            InternalClient.NoDelay = true;
            MessageWriter = new BinaryWriter(InternalClient.GetStream());
            ListenerThread = new Thread(RunCommunications);
            ListenerThread.IsBackground = true;
            ListenerThread.Start();
        }

        /// <summary>
        /// Attempts to connect to a ImpServer using the specified IP address and port number.
        /// </summary>
        /// <param name="ip">The address to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        public void Connect(string ip, int port)
        {
            if(InternalClient is null)
            {
                InternalClient = new TcpClient();
                InternalClient.NoDelay = true;
                InternalClient.Connect(ip, port);
                MessageWriter = new BinaryWriter(InternalClient.GetStream());
                MessageWriter.Write(AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetCustomAttributes<ShareAsAttribute>()).Where(x => x.TypeToShare == GetType()).First().InterfaceBinding.AssemblyQualifiedName);
                ListenerThread = new Thread(RunCommunications);
                ListenerThread.IsBackground = true;
                BinaryReader networkReader = new BinaryReader(InternalClient.GetStream());
                NetworkID = networkReader.ReadUInt16();
                HeldObjects.Add(this);
                ListenerThread.Start();
            }
            else
            {
                throw new InvalidOperationException("The ImpClient is already in use.");
            }
        }

        /// <summary>
        /// Disconnects from the remote host, ending communication between the server and the client.
        /// </summary>
        public void Disconnect()
        {

        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public T CallRemoteMethod<T>(SharedObjectPath obj, object[] arguments, ushort methodID)
        {
            try
            {
                return CallRemoteMethodAsync<T>(obj, arguments, methodID).Result;
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

        [EditorBrowsable(EditorBrowsableState.Never)]
        public Task<T> CallRemoteMethodAsync<T>(SharedObjectPath obj, object[] arguments, ushort methodID)
        {
            AsynchronousNetworkOperation<T> operation = CreateNewAsynchronousNetworkOperation<T>();
            SendImpMessage(new CallRemoteMethodMessage(obj, methodID, arguments, operation.OperationID));
            return operation.Operation;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public T GetRemoteProperty<T>(SharedObjectPath obj, [CallerMemberName] string propertyName = null)
        {
            try {
                return GetRemotePropertyAsync<T>(obj, propertyName).Result;
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

        [EditorBrowsable(EditorBrowsableState.Never)]
        public Task<T> GetRemotePropertyAsync<T>(SharedObjectPath obj, [CallerMemberName] string propertyName = null)
        {
            AsynchronousNetworkOperation<T> operation = CreateNewAsynchronousNetworkOperation<T>();
            SendImpMessage(new GetRemotePropertyMessage(obj, propertyName, operation.OperationID));
            return operation.Operation;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public void SetRemoteProperty<T>(SharedObjectPath obj, T toSet, [CallerMemberName] string propertyName = null)
        {
            try {
                Task t = SetRemotePropertyAsync(obj, toSet, propertyName);
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

        [EditorBrowsable(EditorBrowsableState.Never)]
        public async Task SetRemotePropertyAsync<T>(SharedObjectPath obj, T toSet, [CallerMemberName] string propertyName = null)
        {
            AsynchronousNetworkOperation<object> operation = CreateNewAsynchronousNetworkOperation<object>();
            SendImpMessage(new SetRemotePropertyMessage(obj, propertyName, toSet, operation.OperationID));
            await operation.Operation;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public T GetRemoteIndexer<T>(SharedObjectPath obj, object[] arguments, [CallerMemberName] string propertyName = null)
        {
            return default;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public T SetRemoteIndexer<T>(SharedObjectPath obj, T toSet, object[] arguments, [CallerMemberName] string propertyName = null)
        {
            return toSet;
        }

        public void SendImpMessage(ImpMessage message)
        {
            byte[] toSend = Serializer.Serialize(message);
            MessageWriter.Write(toSend.Length);
            MessageWriter.Write(toSend);
        }

        internal bool SerializeSharedObject(ImpSerializer serializer, BinaryWriter writer, object obj)
        {
            Type objType = obj.GetType();
            ushort? id = SharedTypeBinder.GetIDForRemoteType(objType);
            if(id != null)
            {
                writer.Write(true);
                writer.Write(id.Value);
                serializer.SerializationRuleset[serializer.SerializationTypes[typeof(SharedObjectPath)]].Serializer(serializer, writer, ((RemoteSharedObject)obj).Location);
                return true;
            }
            else
            {
                id = SharedTypeBinder.GetIDForLocalType(objType);
                if(id != null)
                {
                    writer.Write(true);
                    writer.Write(id.Value);
                    SharedObjectPath path;
                    if (HeldObjects.ContainsValue(obj))
                    {
                        path = new SharedObjectPath(NetworkID, HeldObjects[obj]);
                        HeldObjectsData[path.ObjectID]++;
                    }
                    else
                    {
                        path = new SharedObjectPath(NetworkID, HeldObjects.Add(obj));
                        HeldObjectsData[path.ObjectID] = new CountedObject<object>(obj);
                    }
                    serializer.SerializationRuleset[serializer.SerializationTypes[typeof(SharedObjectPath)]].Serializer(serializer, writer, path);
                    return true;
                }
                else
                {
                    writer.Write(false);
                    return false;
                }
            }
        }

        internal object DeserializeSharedObject(ImpSerializer serializer, BinaryReader reader)
        {
            ushort typeID = reader.ReadUInt16();
            SharedObjectPath path = (SharedObjectPath)serializer.SerializationRuleset[serializer.SerializationTypes[typeof(SharedObjectPath)]].Deserializer(serializer, reader);
            if (path.OwnerID == NetworkID) {
                return HeldObjects[path.ObjectID];
            }
            else
            {
                if (RemoteSharedObjects.ContainsKey(path))
                {
                    RemoteSharedObject obj = null;
                    CountedObject<WeakReference<RemoteSharedObject>> counter = RemoteSharedObjects[path];
                    counter++;
                    counter.ReferencedObject.TryGetTarget(out obj);
                    return obj;
                }
                else
                {
                    RemoteSharedObject toReturn = (RemoteSharedObject)Activator.CreateInstance(SharedTypeBinder.GetRemoteType(typeID), path, this);
                    RemoteSharedObjects[path] = new CountedObject<WeakReference<RemoteSharedObject>>(new WeakReference<RemoteSharedObject>(toReturn));
                    return toReturn;
                }
            }
        }

        internal void ReleaseRemoteSharedObject(SharedObjectPath path)
        {
            CountedObject<WeakReference<RemoteSharedObject>> sharedObject;
            RemoteSharedObjects.TryRemove(path, out sharedObject);
            SendImpMessage(new RemoteSharedObjectReleasedMessage(sharedObject.Count, path));
        }

        protected AsynchronousNetworkOperation<T> CreateNewAsynchronousNetworkOperation<T>()
        {
            AsynchronousNetworkOperation<T> toReturn = null;
            CurrentNetworkOperations.Add(x => { return toReturn = new AsynchronousNetworkOperation<T>(x, y => CurrentNetworkOperations.Remove(x)); });
            return toReturn;
        }

        [MessageCallback]
        protected virtual void SetProxyBinderCallback(SetProxyBinderMessage message)
        {
            if(Local)
            {
                SharedTypeBinder = PredefinedProxyBinder.CreateAndBind(message.Interfaces);
            }
        }

        [MessageCallback]
        protected virtual void GetRemoteServerObjectCallback(GetRemoteServerObjectMessage message)
        {
            SendImpMessage(new ReturnRemoteServerObjectMessage(Server));
        }

        [MessageCallback]
        protected virtual void ReturnRemoteServerObjectCallback(ReturnRemoteServerObjectMessage message)
        {
            if(Local && RemoteServer is null)
            {
                RemoteServer = (IImpServer)message.Server;
            }
        }

        [MessageCallback]
        protected virtual async Task CallRemoteMethodCallbackAsync(CallRemoteMethodMessage message)
        {
            object toInvoke = HeldObjects[message.InvocationTarget.ObjectID];
            if (toInvoke is null)
            {
                throw new SecurityException("Remote endpoint attempted to access remote object that it does not hold.");
            }
            else
            {
                if (message.InvocationTarget.OwnerID == NetworkID)
                {
                    try
                    {
                        SendImpMessage(new ReturnRemoteMethodMessage(message.OperationID, await new RemoteMethodInvoker(SharedTypeBinder.GetDataForSharedType(toInvoke.GetType()).Methods[message.MethodID]).Invoke(toInvoke, message.Parameters), null));
                    }
                    catch (Exception e)
                    {
                        e = e.InnerException;
                        SendImpMessage(new ReturnRemoteMethodMessage(message.OperationID, null, new RemoteException(e.GetType().FullName + ": " + e.Message, e.StackTrace, e.Source)));
                    }
                }
                else
                {
                    throw new Exception("Client-client invocation is not supported at this time.");
                }
            }
        }

        [MessageCallback]
        protected virtual void ReturnRemoteMethodCallback(ReturnRemoteMethodMessage message)
        {
            if (message.ExceptionResult is null)
            {
                CurrentNetworkOperations[message.OperatonID].SetResult(message.Result);
            }
            else
            {
                CurrentNetworkOperations[message.OperatonID].SetException(message.ExceptionResult);
            }
        }

        [MessageCallback]
        protected virtual async Task GetRemotePropertyCallbackAsync(GetRemotePropertyMessage message)
        {
            object toInvoke = HeldObjects[message.InvocationTarget.ObjectID];
            if (toInvoke is null)
            {
                throw new SecurityException("Remote endpoint attempted to access remote object that it does not hold.");
            }
            else
            {
                if (message.InvocationTarget.OwnerID == NetworkID)
                {
                    object returnValue = null;
                    try
                    {
                        returnValue = await Task.Factory.StartNew(() => toInvoke.GetType().GetProperty(message.PropertyName).GetValue(toInvoke), CancellationToken.None, TaskCreationOptions.None, RemoteTaskScheduler);
                    }
                    catch (Exception e)
                    {
                        SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, new RemoteException(e.Message, e.StackTrace, e.Source)));
                        return;
                    }
                    SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, returnValue, null));
                }
                else
                {
                    throw new Exception("Client-client invocation is not supported at this time.");
                }
            }
        }

        [MessageCallback]
        protected virtual void ReturnRemotePropertyCallback(ReturnRemotePropertyMessage message)
        {
            if (message.ExceptionResult is null)
            {
                CurrentNetworkOperations[message.OperatonID].SetResult(message.Result);
            }
            else
            {
                CurrentNetworkOperations[message.OperatonID].SetException(message.ExceptionResult);
            }
        }

        [MessageCallback]
        protected virtual async Task SetRemotePropertyCallbackAsync(SetRemotePropertyMessage message)
        {
            object toInvoke = HeldObjects[message.InvocationTarget.ObjectID];
            if (toInvoke is null)
            {
                throw new SecurityException("Remote endpoint attempted to access remote object that it does not hold.");
            }
            else
            {
                if (message.InvocationTarget.OwnerID == NetworkID)
                {
                    try
                    {
                        await Task.Run(() => toInvoke.GetType().GetProperty(message.PropertyName).SetValue(toInvoke, message.Value));
                        SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, null));
                    }
                    catch (Exception e)
                    {
                        SendImpMessage(new ReturnRemotePropertyMessage(message.OperationID, null, new RemoteException(e.Message, e.StackTrace, e.Source)));
                    }
                }
                else
                {
                    throw new Exception("Client-client invocation is not supported at this time.");
                }
            }
        }

        [MessageCallback]
        protected virtual async Task GetRemoteIndexerCallbackAsync(GetRemoteIndexerMessage message)
        {
            object toInvoke = HeldObjects[message.InvocationTarget.ObjectID];
            if (toInvoke is null)
            {
                throw new SecurityException("Remote endpoint attempted to access remote object that it does not hold.");
            }
            else
            {
                if (message.InvocationTarget.OwnerID == NetworkID)
                {
                    object returnValue = null;
                    try
                    {
                        returnValue = await Task.Run(() => toInvoke.GetType().GetProperty(message.PropertyName).GetValue(toInvoke, message.Parameters));
                    }
                    catch (Exception e)
                    {
                        SendImpMessage(new ReturnRemoteMethodMessage(message.OperationID, null, new RemoteException(e.Message, e.StackTrace, e.Source)));
                        return;
                    }
                    SendImpMessage(new ReturnRemoteMethodMessage(message.OperationID, returnValue, null));
                }
                else
                {
                    throw new Exception("Client-client invocation is not supported at this time.");
                }
            }
        }

        [MessageCallback]
        protected virtual void ReturnRemoteIndexerCallback(ReturnRemoteIndexerMessage message)
        {
            if (message.ExceptionResult is null)
            {
                CurrentNetworkOperations[message.OperatonID].SetResult(message.Result);
            }
            else
            {
                CurrentNetworkOperations[message.OperatonID].SetException(message.ExceptionResult);
            }
        }

        private void RunCommunications()
        {
            try
            {
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
                Console.WriteLine(e);
            }
        }

        private void LoadMethodCallbacks()
        {
            foreach (MethodInfo info in GetType().GetMethods(BindingFlags.Instance | BindingFlags.FlattenHierarchy | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if(info.GetCustomAttribute<MessageCallbackAttribute>() != null)
                {
                    MessageCallbacks[info.GetParameters()[0].ParameterType] = info;
                }
            }
        }

        protected class RemoteMethodInvoker
        {
            public MethodInfo Method { get; }
            protected Func<object, object[], Task<object>> GetResult { get; }

            public RemoteMethodInvoker(MethodInfo method) {
                Method = method;
                if(method.ReturnType == typeof(void) || method.ReturnType == typeof(Task))
                {
                    GetResult = async (x, y) => { await Task.Run(() => Method.Invoke(x, y)); return null; };
                }
                else if(typeof(Task).IsAssignableFrom(method.ReturnType))
                {
                    PropertyInfo resultInfo = method.ReturnType.GetProperty(nameof(Task<object>.Result));
                    GetResult = async (x, y) => resultInfo.GetValue(await Task.Run(() => Method.Invoke(x, y)));
                }
                else
                {
                    GetResult = (x, y) => Task.Run(() => Method.Invoke(x, y));
                }
            }

            public virtual Task<object> Invoke(object target, object[] args)
            {
                return GetResult(target, args);
            }
        }
    }

    public class ImpClient<T> : ImpClient where T : IImpServer
    {
        public new T Server => (T)base.Server;
    }
}