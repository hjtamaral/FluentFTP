﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading;

namespace System.Net.FtpClient {
    /// <summary>
    /// ResponseReceived delegate
    /// </summary>
    /// <param name="status">Status number</param>
    /// <param name="response">Status message</param>
    public delegate void ResponseReceived(string status, string response);

    /// <summary>
    /// SecurityNotAvailable delegate
    /// </summary>
    /// <param name="e"></param>
    public delegate void SecurityNotAvailable(FtpSecurityNotAvailable e);

    /// <summary>
    /// Custom log message delegate
    /// </summary>
    /// <param name="originalMessage"></param>
    /// <returns></returns>
    public delegate string CustomLogMessage(string originalMessage);

    /// <summary>
    /// The communication channel for the FTP server / used for issuing commands
    /// and controlling transactions.
    /// </summary>
    public class FtpControlConnection : FtpChannel, IDisposable {
        /// <summary>
        /// Mutex used for locking the command channel while
        /// executing commands
        /// </summary>
        Mutex mCommandLock = new Mutex();

        string _username = null;
        /// <summary>
        /// The username to authenticate with
        /// </summary>
        public string Username {
            get { return _username; }
            set { _username = value; }
        }

        string _password = null;
        /// <summary>
        /// The password to authenticate with
        /// </summary>
        public string Password {
            get { return _password; }
            set { _password = value; }
        }

        int _keepAliveInterval = 0;
        /// <summary>
        /// Sets an interval in seconds to send keep-alive commands to the server
        /// during data transfers. This should not be required, it's 
        /// not even outline in RFC959. The server should not close the
        /// control connection, if it does it's a bug. With that said
        /// it does happen so this feature is there to help work around
        /// it in cases where the server cannot be upgraded. It is a last
        /// ditch effort and unsupported. If it doesn't solve your problem
        /// then contact the administrator of the server where the timeout
        /// occurs and ask them to upgrade their software.
        /// 
        /// A setting of 0 (default) disables this feature. Do not set the value
        /// too low, I recommend starting at about 15 seconds.
        /// </summary>
        public int KeepAliveInterval {
            get { return _keepAliveInterval; }
            set { _keepAliveInterval = value; }
        }

        int _responseReadTimeout = 0;
        /// <summary>
        /// Gets or sets the maximum time in miliseconds in which the control
        /// connection will wait for the server to respond to a command. If the
        /// timeout is exceeded a FtpResponseTimeoutExecption will be thrown.
        /// </summary>
        public int ResponseReadTimeout {
            get { return _responseReadTimeout; }
            set { _responseReadTimeout = value; }
        }

        FtpSslMode _sslMode = FtpSslMode.Explicit;
        /// <summary>
        /// Sets the type of SSL to use. The default is Explicit, meaning SSL is negotiated
        /// after the initial connection, before credentials are sent.
        /// </summary>
        public FtpSslMode SslMode {
            get { return _sslMode; }
            set { _sslMode = value; }
        }

        bool _dataChanEncrypt = true;
        /// <summary>
        /// Enable or disable data channel encryption. This option is only
        /// applicable when the SslMode property is set to use encryption.
        /// The default value is true.
        /// </summary>
        public bool DataChannelEncryption {
            get { return _dataChanEncrypt; }
            set { _dataChanEncrypt = value; }
        }

        bool _enablePipelining = false;
        /// <summary>
        /// Gets / sets a value indicating if we can use pipelining techniques
        /// to talk to the server. If the server allows it, this will help
        /// improve performance on the command channel with large command transactions.
        /// </summary>
        public bool EnablePipelining {
            get { return _enablePipelining; }
            set { _enablePipelining = value; }
        }

        bool m_utf8Enabled = false;
        /// <summary>
        /// Gets a value indicating if UTF8 has been enabled
        /// on this connection
        /// </summary>
        public bool IsUTF8Enabled {
            get { return m_utf8Enabled; }
        }

        event SecurityNotAvailable _secNotAvailable = null;
        /// <summary>
        /// Event is fired when the AUTH command fails for
        /// explicit SSL connections. A cancel property is
        /// provided to allow you to abort the connection.
        /// </summary>
        public event SecurityNotAvailable SecurityNotAvailable {
            add { this._secNotAvailable += value; }
            remove { this._secNotAvailable -= value; }
        }

        event ResponseReceived _responseReceived = null;
        /// <summary>
        /// Event is fired when a message is received from the server. Useful
        /// for logging the conversation with the server.
        /// </summary>
        public event ResponseReceived ResponseReceived {
            add { this._responseReceived += value; }
            remove { this._responseReceived -= value; }
        }

        FtpCapability _caps = FtpCapability.EMPTY;
        /// <summary>
        /// Capabilities of the server
        /// </summary>
        protected FtpCapability Capabilities {
            get {
                if (!this.Connected) {
                    this.Connect();
                }

                if (_caps == FtpCapability.EMPTY) {
                    this.LoadCapabilities();
                }

                return _caps;
            }

            private set {
                _caps = value;
            }
        }

        FtpDataType _currentDataType = 0;
        /// <summary>
        /// Gets the current data type. This value is updated with the SetDataType() method
        /// is called. It is used to avoid the overheaded of executing the command on the 
        /// server when the specified type is already set.
        /// </summary>
        public FtpDataType CurrentDataType {
            get { return _currentDataType; }
            private set { _currentDataType = value; }
        }

        FtpDataChannelType _dataChanType = FtpDataChannelType.ExtendedPassive;
        /// <summary>
        /// The default data channel type to use (default: ExtendedPassive)
        /// </summary>
        public FtpDataChannelType DataChannelType {
            get { return _dataChanType; }
            set { _dataChanType = value; }
        }

        FtpResponseType _respType = FtpResponseType.None;
        /// <summary>
        /// The type of response received from the last command executed
        /// </summary>
        public FtpResponseType ResponseType {
            get { return _respType; }
            private set { _respType = value; }
        }

        string _respCode = null;
        /// <summary>
        /// The status code of the response
        /// </summary>
        public string ResponseCode {
            get { return _respCode; }
            private set { _respCode = value; }
        }

        string _respMessage = null;
        /// <summary>
        /// The message, if any, that the server sent with the response
        /// </summary>
        public string ResponseMessage {
            get { return _respMessage; }
            private set { _respMessage = value; }
        }

        string[] _messages = null;
        /// <summary>
        /// Other informational messages sent from the server
        /// that are not considered part of the response
        /// </summary>
        public string[] Messages {
            get { return _messages; }
            private set { _messages = value; }
        }

        /// <summary>
        /// General success or failure of the last command executed
        /// </summary>
        public bool ResponseStatus {
            get {
                if (this.ResponseCode != null) {
                    int i = int.Parse(this.ResponseCode[0].ToString());

                    // 1xx, 2xx, 3xx indicate success
                    // 4xx, 5xx are failures
                    if (i >= 1 && i <= 3) {
                        return true;
                    }
                }

                return false;
            }
        }

        int _dataChannelReadTimeout = -1;
        /// <summary>
        /// Gets or sets the time in miliseconds that a data channel will
        /// wait for the server to responde before throwing an IOException
        /// </summary>
        public int DataChannelReadTimeout {
            get { return _dataChannelReadTimeout; }
            set { _dataChannelReadTimeout = value; }
        }

        /// <summary>
        /// Acquire an exclusive lock on the command channel
        /// while executing/processing commands
        /// </summary>
        public void LockControlConnection() {
            this.mCommandLock.WaitOne();
        }

        /// <summary>
        /// Acquire an exclusive lock on the command channel
        /// while executing/processing commands 
        /// </summary>
        /// <param name="timeout"></param>
        public void LockControlConnection(int timeout) {
            this.mCommandLock.WaitOne(timeout);
        }

        /// <summary>
        /// Release the exclusive lock held on the command channel
        /// </summary>
        public void UnlockControlConnection() {
            this.mCommandLock.ReleaseMutex();
        }

        /// <summary>
        /// Fires the response received event.
        /// </summary>
        /// <param name="status">Status code</param>
        /// <param name="response">Status message</param>
        protected void OnResponseReceived(string status, string response) {
            if (this._responseReceived != null) {
                this._responseReceived(status, response);
            }
        }

        /// <summary>
        /// Delegate used for read response timeout
        /// </summary>
        /// <returns></returns>
        delegate string GetLineFromSocket();

        /// <summary>
        /// Reads a line from the FTP channel socket. Use with discretion,
        /// can cause the code to freeze if you're trying to read data when no data
        /// is being sent.
        /// </summary>
        /// <returns></returns>
        protected virtual string ReadLine() {
            if (this.StreamReader != null) {
                string buf; // = this.StreamReader.ReadLine();

                if (this.ResponseReadTimeout > 0) {
                    GetLineFromSocket GetLine = new GetLineFromSocket(this.StreamReader.ReadLine);
                    IAsyncResult ar = GetLine.BeginInvoke(null, null);

                    ar.AsyncWaitHandle.WaitOne(this.ResponseReadTimeout);
                    if (!ar.IsCompleted) {
                        // close the socket because we'll need to reconnect to recover
                        // from this failure
                        this.Socket.Close();
                        throw new FtpResponseTimeoutException("Timed out waiting for the server to respond to the last command.");
                    }

                    buf = GetLine.EndInvoke(ar);
                }
                else {
                    buf = this.StreamReader.ReadLine();
                }

                WriteLineToLogStream(string.Format("> {0}", buf));
                this.LastSocketActivity = DateTime.Now;

                return buf;
            }

            throw new IOException("The reader object is null. Are we connected?");
        }

        /// <summary>
        /// Reads bytes off the socket
        /// </summary>
        /// <param name="buf"></param>
        /// <param name="offset"></param>
        /// <param name="size"></param>
        protected virtual int Read(byte[] buf, int offset, int size) {
            if (this.BaseStream != null) {
                this.LastSocketActivity = DateTime.Now;
                return this.BaseStream.Read(buf, 0, size);
            }

            throw new IOException("The network stream is null. Are we connected?");
        }

        /// <summary>
        /// Writes the specified byte array to the network stream
        /// </summary>
        /// <param name="buf"></param>
        protected virtual void Write(byte[] buf) {
            this.Write(buf, 0, buf.Length);
        }

        /// <summary>
        /// Writes the specified byte array to the network stream
        /// </summary>
        protected virtual void Write(byte[] buf, int offset, int count) {
            if (this.BaseStream != null) {
                if (this.NeedsSocketPoll && !this.PollConnection()) {
                    // we've been disconnected, try to reconnect
                    this.Disconnect();
                    this.Connect();
                }

                this.BaseStream.Write(buf, offset, count);
                this.LastSocketActivity = DateTime.Now;
            }
            else {
                throw new IOException("The network stream is null. Are we connected?");
            }
        }

        /// <summary>
        /// Writes a line to the channel with the correct line endings.
        /// </summary>
        /// <param name="line">Format</param>
        /// <param name="args">Parameters</param>
        protected virtual void WriteLine(string line, params object[] args) {
            this.WriteLine(line, args);
        }

        /// <summary>
        /// Writes a line to the channel with the correct line endings.
        /// </summary>
        /// <param name="line">The line to write</param>
        protected virtual void WriteLine(string line) {
            this.Write(string.Format("{0}\r\n", line));
        }

        /// <summary>
        /// Writes the specified data to the network stream in the proper encoding
        /// </summary>
        protected virtual void Write(string format, params object[] args) {
            this.Write(string.Format(format, args));
        }

        /// <summary>
        /// Writes the specified data to the network stream in the proper encoding
        /// </summary>
        /// <param name="data"></param>
        protected virtual void Write(string data) {
            string traceout = null;

            if (data.ToUpper().StartsWith("PASS")) {
                traceout = "< PASS [omitted for security]";
            }
            else {
                traceout = string.Format("< {0}", data.Trim('\n').Trim('\r'));
            }

            WriteLineToLogStream(traceout);

            //if (_caps != FtpCapability.EMPTY && this.HasCapability(FtpCapability.UTF8)) {
            if (this.IsUTF8Enabled) {
                this.Write(Encoding.UTF8.GetBytes(data));
            }
            else {
                this.Write(Encoding.Default.GetBytes(data));
            }
        }

        DateTime _lastSockActivity = DateTime.MinValue;
        /// <summary>
        /// Gets a the last time data was read or written to the socket.
        /// </summary>
        protected DateTime LastSocketActivity {
            get {
                if (_lastSockActivity == DateTime.MinValue) {
                    // we just connected so set the
                    // value to now
                    _lastSockActivity = DateTime.Now;
                }

                return _lastSockActivity;
            }
            private set { _lastSockActivity = value; }
        }

        /// <summary>
        /// Returns true if the last socket poll was 30 seconds ago. The last poll
        /// time gets updated every time data is read or written to the socket.
        /// </summary>
        protected bool NeedsSocketPoll {
            get {
                DateTime lastPoll = this.LastSocketActivity;

                if (Math.Round(DateTime.Now.Subtract(lastPoll).TotalSeconds) > 30) {
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Attempts to check our connectivity to the server
        /// with using Socket.Poll
        /// </summary>
        /// <returns>True if connected, false otherwise</returns>
        protected bool PollConnection() {
            if (this.Socket.Poll(500000, SelectMode.SelectRead) && this.Socket.Available == 0) {
                // we've been disconnected, probably due to inactivity
                return false;
            }

            return true;
        }

        /// <summary>
        /// Executes a command on the server
        /// </summary>
        /// <param name="cmd"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool Execute(string cmd, params object[] args) {
            return this.Execute(string.Format(cmd, args));
        }

        /// <summary>
        /// Executes a command on the server. 
        /// </summary>
        /// <param name="cmd"></param>
        /// <returns></returns>
        public bool Execute(string cmd) {
            if (!this.Connected) {
                this.Connect();
            }

            this.WriteLine(cmd);

            return this.ReadResponse();
        }

        /// <summary>
        /// Reads and parses the response a command that was executed. Do not call this
        /// unless you just executed a command, will cause code to freeze waiting for the
        /// server to send data that is never comming.
        /// </summary>
        /// <returns></returns>
        public bool ReadResponse() {
            string buf;
            List<string> messages = new List<string>();

            this.ResponseType = FtpResponseType.None;
            this.ResponseCode = null;
            this.ResponseMessage = null;
            this.Messages = null;

            while ((buf = this.ReadLine()) != null) {
                Match m = Regex.Match(buf, @"^(\d{3})\s(.*)$");

                if (m.Success) { // the server sent the final response message
                    if (m.Groups.Count > 1) {
                        this.ResponseCode = m.Groups[1].Value;
                    }

                    if (m.Groups.Count > 2) {
                        this.ResponseMessage = m.Groups[2].Value;
                    }

                    if (messages.Count > 0) {
                        this.Messages = messages.ToArray();
                    }

                    // check response
                    if (this.ResponseCode != null) {
                        this.ResponseType = (FtpResponseType)int.Parse(this.ResponseCode[0].ToString());
                        this.OnResponseReceived(this.ResponseCode, this.ResponseMessage);
                        return this.ResponseStatus;
                    }

                    throw new FtpException("Could not determine the response status");
                }
                else {
                    this.OnResponseReceived("INFO", buf);
                }

                messages.Add(buf);
            }

            throw new FtpException("An unknown error occurred while executing the command");
        }

        /// <summary>
        /// Open a connection
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        public virtual void Connect(string host, int port) {
            if (!this.Connected) {
                this.Server = host;
                this.Port = port;
                this.Connect();
            }
        }

        /// <summary>
        /// Open a connection
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        public virtual void Connect(IPAddress ip, int port) {
            if (!this.Connected) {
                this.Server = ip.ToString();
                this.Port = port;
                this.Connect();
            }
        }

        /// <summary>
        /// Open a connection
        /// </summary>
        /// <param name="ipep"></param>
        public virtual void Connect(IPEndPoint ipep) {
            if (!this.Connected) {
                this.Server = ipep.Address.ToString();
                this.Port = ipep.Port;
                this.Connect();
            }
        }

        /// <summary>
        /// Checks if the server supports the specified capability
        /// </summary>
        /// <param name="cap"></param>
        public bool HasCapability(FtpCapability cap) {
            //return (this.Capabilities & cap) == cap;
            return this.Capabilities.HasFlag(cap);
        }

        /// <summary>
        /// Removes the specified capability from the list
        /// </summary>
        /// <param name="cap"></param>
        public void RemoveCapability(FtpCapability cap) {
            this.Capabilities &= ~(cap);
        }

        /// <summary>
        /// Loads the capabilities of this server
        /// </summary>
        private void LoadCapabilities() {
#if DEBUG
            // trying to figure out why capabilities are being loading
            // multiple times upon connection
            //Debug.Print(Environment.StackTrace.ToString());
#endif
            if (this.Execute("FEAT")) {
                // some servers support EPSV but do not advertise it
                // in the FEAT list. for this reason, we assume EPSV
                // is supported and if we get a 500 reply then we fall back
                // to PASV.
                this._caps = FtpCapability.EPSV | FtpCapability.EPRT;

                foreach (string feat in this.Messages) {
                    if (feat.ToUpper().Contains("MLST") || feat.ToUpper().Contains("MLSD"))
                        this._caps |= FtpCapability.MLSD | FtpCapability.MLST;
                    else if (feat.ToUpper().Contains("MDTM"))
                        this._caps |= (FtpCapability.MDTM | FtpCapability.MDTMDIR);
                    else if (feat.ToUpper().Contains("REST STREAM"))
                        this._caps |= FtpCapability.REST;
                    else if (feat.ToUpper().Contains("SIZE"))
                        this._caps |= FtpCapability.SIZE;
                    else if (feat.ToUpper().Contains("UTF8"))
                        this._caps |= FtpCapability.UTF8;
                    else if (feat.ToUpper().Contains("PRET"))
                        this._caps |= FtpCapability.PRET;
                    // EPSV and EPRT are already assumed to be supported.
                    //else if(feat.ToUpper().Contains("EPSV") || feat.ToUpper().Contains("EPRT"))
                    //	this.Capabilities |= FtpCapability.EPSV | FtpCapability.EPRT;
                }
            }
            else {
                this._caps = FtpCapability.NONE;
            }
        }

        /// <summary>
        /// Gets the size of the specified file. If there are any errors getting the file size, 0 will be returned
        /// rather than throwing an exception, even if the file doesn't exist.
        /// </summary>
        /// <param name="path">The full or relative (to the current working directory) path</param>
        /// <returns>The file size, 0 if there was a problem executing the command or parsing the size</returns>
        public long GetFileSize(string path) {
            long size = 0;

            if (this.HasCapability(FtpCapability.SIZE)) {
                Match m;

                try {
                    this.LockControlConnection();

                    // change to binary before executing this command
                    this.SetDataType(FtpDataType.Binary);

                    // ignore errors, return 0 if there is one. some servers
                    // don't support large file sizes.
                    if (this.Execute("SIZE {0}", path)) {
                        m = Regex.Match(this.ResponseMessage, @"(\d+)");
                        if (m.Success && !long.TryParse(m.Groups[1].Value, out size)) {
                            size = 0;
                        }
                    }
                }
                finally {
                    this.UnlockControlConnection();
                }
            }

            return size;
        }

        /// <summary>
        /// Set the data type for the data channel
        /// </summary>
        /// <param name="datatype"></param>
        protected void SetDataType(FtpDataType datatype) {
            // don't execute the command if the requested
            // data type is already set.
            if (this.CurrentDataType == datatype)
                return;

            switch (datatype) {
                case FtpDataType.Binary:
                    this.Execute("TYPE I");
                    break;
                case FtpDataType.ASCII:
                    this.Execute("TYPE A");
                    break;
            }

            this.CurrentDataType = datatype;

            if (!this.ResponseStatus) {
                throw new FtpCommandException(this);
            }
        }

        /// <summary>
        /// Set the transfer mode for the data channel. If block
        /// mode is requested and it fails, stream mode is
        /// automatically used.
        /// </summary>
        /// <param name="mode"></param>
        protected void SetDataMode(FtpDataMode mode) {
            switch (mode) {
                case FtpDataMode.Block:
                    throw new FtpException("Block mode transfers have not and will not be implemented.");
                case FtpDataMode.Stream:
                    this.Execute("MODE S");
                    break;
            }

            if (!this.ResponseStatus) {
                throw new FtpCommandException(this);
            }
        }

        /// <summary>
        /// Opens a binary data stream
        /// </summary>
        /// <returns>A stream that can be read or written depending on the type of transaction, but not both.</returns>
        protected FtpDataStream OpenDataStream() {
            return this.OpenDataStream(FtpDataType.Binary);
        }

        /// <summary>
        /// Opens a data stream using the data format specified
        /// </summary>
        /// <param name="type">Data format to use</param>
        /// <returns>A stream that can be read or written depending on the type of transaction, but not both.</returns>
        protected FtpDataStream OpenDataStream(FtpDataType type) {
            FtpDataStream stream = null;

            this.SetDataType(type);

            switch (this.DataChannelType) {
                case FtpDataChannelType.Passive:
                case FtpDataChannelType.ExtendedPassive:
                    stream = new FtpPassiveStream(this);
                    break;
                case FtpDataChannelType.Active:
                case FtpDataChannelType.ExtendedActive:
                    stream = new FtpActiveStream(this);
                    break;
            }

            if (stream != null) {
                stream.ReadTimeout = this.DataChannelReadTimeout;
            }

            return stream;
        }

        /// <summary>
        /// Opens a stream to the specified file on the server
        /// </summary>
        /// <param name="path">The full or relative path to the file</param>
        /// <param name="access">Read, write or append to the file. Append ignores the offset parameter. Use read or write accordingly if you want to open the file to a specific location.</param>
        /// <returns>A non seekable stream to the file.</returns>
        /// <example>
        ///     This example attempts to illustrate a stream based file download.
        ///     <code source="..\Examples\DownloadStream\Program.cs" lang="cs"></code>
        /// </example>
        public FtpDataStream OpenFile(string path, FtpFileAccess access) {
            return this.OpenFile(path, FtpDataType.Binary, access, 0);
        }

        /// <summary>
        /// Opens a stream to the specified file on the server
        /// </summary>
        /// <param name="path">The full or relative path to the file</param>
        /// <param name="access">Read, write or append to the file. Append ignores the offset parameter. Use read or write accordingly if you want to open the file to a specific location.</param>
        /// <param name="offset">Starting position of the stream. Please note this parameter is ignored for FtpFileAccess.Append.</param>
        /// <returns>A non seekable stream to the file.</returns>
        /// <example>
        ///     This example attempts to illustrate a stream based file download.
        ///     <code source="..\Examples\DownloadStream\Program.cs" lang="cs"></code>
        /// </example>
        public FtpDataStream OpenFile(string path, FtpFileAccess access, long offset) {
            return this.OpenFile(path, FtpDataType.Binary, access, offset);
        }

        /// <summary>
        /// Opens a stream to the specified file on the server
        /// </summary>
        /// <param name="path">The full or relative path to the file</param>
        /// <param name="type">ASCII/Binary</param>
        /// <param name="access">Starting position of the stream. Please note this parameter is ignored for FtpFileAccess.Append.</param>
        /// <returns>A non seekable stream to the file.</returns>
        /// <example>
        ///     This example attempts to illustrate a stream based file download.
        ///     <code source="..\Examples\DownloadStream\Program.cs" lang="cs"></code>
        /// </example>
        public FtpDataStream OpenFile(string path, FtpDataType type, FtpFileAccess access) {
            return this.OpenFile(path, type, access, 0);
        }

        /// <summary>
        /// Opens a stream to the specified file on the server
        /// </summary>
        /// <param name="path">The full or relative path to the file</param>
        /// <param name="type">ASCII/Binary</param>
        /// <param name="access">Read, write or append to the file. Append ignores the offset parameter. Use read or write accordingly if you want to open the file to a specific location.</param>
        /// <param name="offset">Starting position of the stream. Please note this parameter is ignored for FtpFileAccess.Append.</param>
        /// <returns>A non seekable stream to the file.</returns>
        /// <example>
        ///     This example attempts to illustrate a stream based file download.
        ///     <code source="..\Examples\DownloadStream\Program.cs" lang="cs"></code>
        /// </example>
        public FtpDataStream OpenFile(string path, FtpDataType type, FtpFileAccess access, long offset) {
            FtpDataStream stream = this.OpenDataStream(type);
            string cmd = null;

            switch (access) {
                case FtpFileAccess.Read:
                    cmd = string.Format("RETR {0}", path);
                    break;
                case FtpFileAccess.Write:
                    cmd = string.Format("STOR {0}", path);
                    break;
                case FtpFileAccess.Append:
                    cmd = string.Format("APPE {0}", path);
                    break;
            }

            stream.SetLength(this.GetFileSize(path));

            switch (access) {
                case FtpFileAccess.Read:
                case FtpFileAccess.Write:
                    if (offset > 0)
                        stream.Seek(offset);
                    break;
            }

            if (!stream.Execute(cmd)) {
                stream.Dispose();
                throw new FtpCommandException(this);
            }

            return stream;
        }

        /// <summary>
        /// Terminates ftp session and cleans up the resources
        /// being used.
        /// </summary>
        public override void Disconnect() {
            if (this.Connected) {
                bool disconnected = (this.Socket.Poll(50000, SelectMode.SelectRead) && this.Socket.Available == 0);

                if (!disconnected && !this.Execute("QUIT")) {
                    // we don't want to do this, the user is 
                    // trying to terminate the connection.
                    //throw new FtpCommandException(this);
                }
            }

            base.Disconnect();
        }

        /// <summary>
        /// Upon the initial connection, we will be presented with a banner and status
        /// </summary>
        void OnInitalizedConnection(FtpChannel c) {
            // clear out the capabilities flag upon
            // connection to force a re-load if this
            // is a reconnection
            this.Capabilities = FtpCapability.EMPTY;
            // if this is a new connection on an existing object we may not
            // have UTF8 so make sure we reset this property accordingly.
            this.m_utf8Enabled = false;

            if (this.SslMode == FtpSslMode.Implicit) {
                // The connection should already be encrypted
                // so authenticate the connection and then
                // try to read the initial greeting.
                this.AuthenticateConnection();
            }


            if (!this.ReadResponse()) {
                this.Disconnect();
                throw new FtpCommandException(this);
            }

            if (this.SslMode == FtpSslMode.Explicit) {
                if (this.Execute("AUTH TLS") || this.Execute("AUTH SSL")) {
                    this.AuthenticateConnection();
                }
                else if (this._secNotAvailable != null) {
                    FtpSecurityNotAvailable secna = new FtpSecurityNotAvailable(this);

                    this._secNotAvailable(secna);

                    if (secna.Cancel) {
                        throw new FtpCommandException(this);
                    }
                }
            }

            if (this.SslEnabled && this.DataChannelEncryption) {
                if (!this.Execute("PBSZ 0")) {
                    // do nothing? some severs don't even
                    // care if you execute PBSZ however rfc 4217
                    // says that PBSZ is required if you want
                    // data channel security.
                    //throw new FtpCommandException(this);
#if DEBUG
                    System.Diagnostics.Debug.WriteLine("PBSZ ERROR: " + this.ResponseMessage);
#endif
                }

                if (!this.Execute("PROT P")) { // turn on data channel protection.
                    throw new FtpCommandException(this);
                }
            }

            this.Login();
        }

        /// <summary>
        /// This is the login event handler. It performs the FTP login
        /// if a connection to the server has been made.
        /// </summary>
        void Login() {
            try {
                this.LockControlConnection();

                if (this.Username != null) {
                    if (!this.Execute("USER {0}", this.Username)) {
                        throw new FtpCommandException(this);
                    }

                    if (this.ResponseType == FtpResponseType.PositiveIntermediate) {
                        if (this.Password == null) {
                            throw new FtpException("The server is asking for a password but it has not been set.");
                        }

                        if (!this.Execute("PASS {0}", this.Password)) {
                            throw new FtpCommandException(this);
                        }
                    }
                }

                // turn on UTF8 if it's available
                //this.EnableUTF8();
                if (!this.IsUTF8Enabled && this.HasCapability(FtpCapability.UTF8)) {
                    if (this.Execute("OPTS UTF8 ON")) {
                        this.m_utf8Enabled = true;
                    }
                }
            }
            finally {
                this.UnlockControlConnection();
            }
        }

        /// <summary>
        /// Initalize a new command channel object.
        /// </summary>
        public FtpControlConnection() {
            this.ConnectionReady += new FtpChannelConnected(OnInitalizedConnection);
        }

        private FtpTraceListener TraceListener = new FtpTraceListener();

        CustomLogMessage _logMessage;
        /// <summary>
        /// Custom log message delegate
        /// </summary>
        public CustomLogMessage LogMessage {
            get { return _logMessage; }
            set { _logMessage = value; }
        }

        /// <summary>
        /// Gets or sets a stream to log FTP transactions to. Can be
        /// used for logging to a file, the console window, or what have you.
        /// </summary>
        public Stream FtpLogStream {
            get { return TraceListener.OutputStream; }
            set { TraceListener.OutputStream = value; }
        }

        /// <summary>
        /// Gets or sets a value that indicates if the
        /// output stream should be flushed everytime
        /// a log enter is written to it.
        /// </summary>
        public bool FtpLogFlushOnWrite {
            get { return TraceListener.FlushOnWrite; }
            set { TraceListener.FlushOnWrite = value; }
        }

        /// <summary>
        /// Writes a message to the FTP log stream
        /// </summary>
        /// <param name="message"></param>
        public void WriteToLogStream(string message) {
            if (LogMessage != null) {
                message = LogMessage(message);
            }
            TraceListener.Write(message);
        }

        /// <summary>
        /// Writes a line to the FTP log stream
        /// </summary>
        /// <param name="message"></param>
        public void WriteLineToLogStream(string message) {
            if (LogMessage != null) {
                message = LogMessage(message);
            }
            TraceListener.WriteLine(message);
        }
    }
}
