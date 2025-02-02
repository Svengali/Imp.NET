﻿using System;
using System.Collections.Generic;
using System.Text;

namespace DouglasDwyer.Imp.Messages
{
    internal class ReturnRemotePropertyMessage : ImpMessage
    {
        public ushort OperatonID;
        public object Result;
        public RemoteException ExceptionResult;

        public ReturnRemotePropertyMessage(ushort operationID, object result, RemoteException exceptionResult)
        {
            OperatonID = operationID;
            Result = result;
            ExceptionResult = exceptionResult;
        }
    }
}
