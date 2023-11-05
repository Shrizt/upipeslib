using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace uPipesLib
{

    public class EAHostClientEvent : EventArgs
    {
        public string pipeOutName, message;
        public int status;
        public int etype; //0-message, 1-connected, 2-status changed, 3-disconnected        


        public EAHostClientEvent(uPipeExchange upe, int etype, string message="")
        {            
            pipeOutName = upe.pipeOutName;            
            status = upe.status;
            this.message = message;
            this.etype = etype;
        }
    }

    public class uPHostManager /////////////class to handle many client-server pairs
    {

        private List<uPipeExchange> srvInstances = new List<uPipeExchange>();
        public string serverPipeName { get; private set; }
        public int instancesCreated { get; private set; }

        public event EventHandler<EAHostClientEvent> ClientMessageReceived;
        public event EventHandler<EAHostClientEvent> ClientDisconnected;
        public event EventHandler<EAHostClientEvent> ClientConnected;
        public event EventHandler<EAHostClientEvent> ClientStatusChanged;



        protected virtual void OnClientMessageReceived(uPipeExchange upe, string message)
        {
            ClientMessageReceived?.Invoke(this, new EAHostClientEvent(upe, 0, message));
        }

        protected virtual void OnClientConnected(uPipeExchange upe, string message="")
        {
            ClientConnected?.Invoke(this, new EAHostClientEvent(upe, 1, message));
        }

        protected virtual void OnClientDisonnected(uPipeExchange upe, string message = "")
        {
            ClientDisconnected?.Invoke(this, new EAHostClientEvent(upe, 3, message));
        }
        protected virtual void OnClientStatusChanged(uPipeExchange upe, int newstatus)
        {
            var args = new EAHostClientEvent(upe,2);
            args.status = newstatus;
            ClientStatusChanged?.Invoke(this, args);
        }


        private void h_MessagePrint(object sender, string message)
        {         
            OnClientMessageReceived((uPipeExchange)sender, message);
        }

        private void h_PipeBroken(object sender, string message)
        {            
                OnClientDisonnected((uPipeExchange)sender, message);                
        }

        private void h_StatusChanged(object sender, int newstatus)
        {
            OnClientStatusChanged((uPipeExchange)sender, newstatus);            
        }


        private void manageInstances()
        {
            instancesCreated = 0;
            while (true)
            {
                instancesCreated++;
                srvInstances.Add(new uPipeExchange(serverPipeName));                
                srvInstances.Last().MessageReceived += h_MessagePrint;
                srvInstances.Last().PipeBroken += h_PipeBroken;
                srvInstances.Last().StatusChanged += h_StatusChanged;
                srvInstances.Last().StartServer();
                if (srvInstances.Last().isConnected()) OnClientConnected(srvInstances.Last());                

                for (int i = srvInstances.Count - 1; i >= 0; i--)
                {
                    if (!srvInstances[i].isConnected()) srvInstances.RemoveAt(i);
                }                

            }
        }

        public int SendAll(string message)
        {
            int i = 0;
            foreach (uPipeExchange srv in srvInstances)
            {
                if (srv.isConnected()) srv.MessageOut(message);
                i++;
            }
            return i;
        }

        public bool SendToPipe(string pipeName, string message)
        {
            uPipeExchange target = srvInstances.Find(item => item.pipeOutName == pipeName);
            if (target == null) return false;
            target.MessageOut(message);
            return true;
        }

        public int ClientsCount()
        {
            return srvInstances.Count;
        }

        public uPHostManager(string _serverPipeName)
        {
            serverPipeName = _serverPipeName;
            Task task = Task.Run((Action)manageInstances);
        }

    }


    public class uPipeExchange
    {
        public NamedPipeServerStream pipeIn = null;
        public NamedPipeClientStream pipeOut = null;

        public StreamWriter streamWriter = null;
        public StreamReader streamReader = null;
        private PipeSecurity pipeSecurity = new PipeSecurity();
        public string serverPipeName;
        public string pipeOutName { get; set; }
        public string pipeInName { get; set; }        

        public List<string> outMsgs = new List<string>();
        public List<string> outMsgsCopy = new List<string>();

        public Task handleOut, handleIn;
        public int outSleepDelay = 100; //ms to sleep waiting for outgoing msgs
        public int status = 0;

        public event EventHandler<string> MessageReceived;
        public event EventHandler<string> PipeBroken;
        public event EventHandler<int> StatusChanged;

        private System.Timers.Timer timerDisconnect;

        public uPipeExchange(string serverPipeName)
        {
            this.serverPipeName = serverPipeName;
            pipeSecurity.AddAccessRule(new PipeAccessRule("Everyone", PipeAccessRights.ReadWrite, AccessControlType.Allow));
            pipeSecurity.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().Owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        public bool StartServer()
        {
            try
            {
                pipeInName = serverPipeName; //for server
                pipeIn = new NamedPipeServerStream(this.pipeInName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512, 512, pipeSecurity);
                OnStatusChanged(11);
                pipeIn.WaitForConnection();
                if (pipeIn.IsConnected)
                {
                    OnStatusChanged(12);
                    Console.WriteLine("Client connected!");
                    streamReader = new StreamReader(pipeIn);

                    pipeOutName = streamReader.ReadLine(); //read incoming connection init string // tbd debug                    
                    Console.WriteLine($"Opening pipeOut with name {pipeOutName}");
                    OnStatusChanged(13);
                    pipeOut = new NamedPipeClientStream(".", pipeOutName, PipeDirection.Out);
                    
                    SetTimerDisconnect(2000);
                    pipeOut.Connect();
                    if (pipeOut.IsConnected) timerDisconnect.Stop();
                    
                    OnStatusChanged(14);
                    streamWriter = new StreamWriter(pipeOut);
                    streamWriter.AutoFlush = true;

                    handleIn = HandleInMessagesAsync(); //start handle task
                    handleOut = HandleOutMessagesAsync();
                    OnStatusChanged(15);
                    return true;
                }
                OnStatusChanged(19);
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error StartServer: {ex.Message}");
                OnStatusChanged(19);
                return false;
            }
        }

        public bool StartClient(string clientPipeName)
        {
            pipeOutName = serverPipeName;
            pipeInName = clientPipeName;
            try
            {
                pipeIn = new NamedPipeServerStream(pipeInName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512, 512, pipeSecurity);
                pipeOut = new NamedPipeClientStream(".", pipeOutName, PipeDirection.Out);
                OnStatusChanged(21);

                pipeOut.Connect();
                if (pipeOut.IsConnected)
                    OnStatusChanged(22);
                else { OnStatusChanged(290); OnPipeBroken("pipeOut not connected"); return false; }
                streamWriter = new StreamWriter(pipeOut);
                streamWriter.AutoFlush = true;
                streamWriter.WriteLine(pipeInName); //send pipeIn name to server 
                SetTimerDisconnect(2000);
                pipeIn.WaitForConnection();
                if (pipeIn.IsConnected)
                {
                    timerDisconnect.Stop(); 
                    OnStatusChanged(23);
                    streamReader = new StreamReader(pipeIn);

                    handleIn = HandleInMessagesAsync(); //start handle task
                    handleOut = HandleOutMessagesAsync();
                    OnStatusChanged(24);
                    return true;
                }
                OnStatusChanged(291);
                OnPipeBroken("pipeIn not connected");
                return false;
            }
            catch (Exception ex)
            {
                OnStatusChanged(292);
                OnPipeBroken(ex.Message);
                return false;
            }
        }

        public async Task HandleInMessagesAsync()
        {
            try
            {

                while (pipeIn.IsConnected)
                {
                    string message = await streamReader.ReadLineAsync();
                    if (!string.IsNullOrEmpty(message))
                    {
                        //Console.WriteLine($"Received message: {message}");
                        OnMessageReceived(message);
                    }
                }
                OnStatusChanged(38);
                OnPipeBroken("HandleInMessagesAsync: pipeIn disconnected");
            }
            catch (Exception ex)
            {
                OnStatusChanged(391);
                OnPipeBroken(ex.Message);
            }
        }
        public async Task HandleOutMessagesAsync()
        {
            try
            {
                while (pipeOut.IsConnected)
                {
                    foreach (string message in outMsgs)
                        outMsgsCopy.Add(message);
                    outMsgs.Clear();
                    foreach (string message in outMsgsCopy)
                        if (!string.IsNullOrEmpty(message))
                        {
                            await streamWriter.WriteLineAsync(message);
                            Console.WriteLine("Message sent!"); ///
                        }
                    await streamWriter.FlushAsync();
                    outMsgsCopy.Clear();
                    while (outMsgs.Count == 0)
                        Thread.Sleep(outSleepDelay);
                }
                OnStatusChanged(491);
                OnPipeBroken("pipeOut disconnected");
            }
            catch (Exception ex)
            {
                OnStatusChanged(492);
                OnPipeBroken(ex.Message);
            }
        }

        private void SetTimerDisconnect(int ms)
        {
            // Create a timer with a two second interval.
            timerDisconnect = new System.Timers.Timer(ms);
            // Hook up the Elapsed event for the timer. 
            timerDisconnect.Elapsed += OnTimerDisconnect;
            timerDisconnect.AutoReset = false;
            timerDisconnect.Enabled = true;
        }

        private void OnTimerDisconnect(Object source, ElapsedEventArgs e)
        {
            ClosePipes(); 
        }

        public int MessageOut(string message)
        {
            outMsgs.Add(message);
            return outMsgs.Count;
        }

        public void ClosePipes()
        {
            if (streamWriter is StreamWriter) streamWriter.Dispose();
            if (streamReader is StreamReader) streamReader.Dispose();
            if (!(pipeIn is null)) pipeIn.Dispose();
            if (!(pipeOut is null)) pipeOut.Dispose();
            OnStatusChanged(999);
        }

        public bool isConnected()
        {
            return (!(pipeOut is null) && pipeOut.IsConnected && !(pipeIn is null) && pipeIn.IsConnected);
        }

        protected virtual void OnMessageReceived(string msg)
        {
            MessageReceived?.Invoke(this, msg);
        }

        protected virtual void OnPipeBroken(string errormsg)
        {
            if (streamReader != null)
            {
                ClosePipes();
                PipeBroken?.Invoke(this, errormsg);
            }
        }

        protected virtual void OnStatusChanged(int newstatus)
        {
            StatusChanged?.Invoke(this, newstatus);
            status = newstatus;
        }

    }



}
