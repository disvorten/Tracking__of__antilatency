using Antilatency.DeviceNetwork;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System;
using System.Threading.Tasks;
using Antilatency.Alt.Environment;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.Json;
using Antilatency.Alt.Tracking;
class Program
{
    private static readonly string date = DateTime.Now.ToString("ddHHmmss");
    private readonly string Columns = "Mode;Timestamp;SocketTag;Rotation;Position;AngVelocity;Velocity;Stage;Value";
    private string full_path;
    private static string Root_path;
    private static Dictionary<string, string> correct_names = new Dictionary<string, string>();
    private static readonly string Config_Path = Path.GetFullPath("Config.txt");
    private static readonly string Player_Config_Path = Path.GetFullPath("Player_Config.txt"); 
    private static string Mode;
    private static string Painting_Mode;
    private delegate void Button(char message);
    private static event Button Pressed_Button;
    private delegate void Button_deactivate(char message);
    private static char Button_state = ' ';
    static void Main()
    {
        using (StreamReader sr = new(Config_Path))
        {
            string[] lines = sr.ReadToEnd().Split('\n');
            int Len = lines.Length;
            int Hash_Count = 0;
            for (int i = 0; i < Len; i++)
            {
                if (lines[i][0] == '#') { Hash_Count += 1; continue; }
                if (Hash_Count == 1) { string[] tmp = lines[i].Split(','); correct_names.Add(tmp[0], tmp[1]); }
                if (Hash_Count == 2) Mode = lines[i].Split(',')[0].Substring(0, lines[i].Length - 1);
                if (Hash_Count == 3) Painting_Mode = lines[i].Split(',')[0].Substring(0, lines[i].Length - 1);
                if (Hash_Count == 4) Root_path = lines[i].Split(',')[0];
            }
            sr.Close();
        }
        var path = Root_path + $"Button_{date}.csv";
        if(Painting_Mode != "Paint")
        {
            using (StreamWriter sw = new(path))
            {
                sw.WriteLine("Mode;Button;Timestamp");
                sw.Close();
            }
        }
        Pressed_Button += Write_With_Button;
        using var storageClientLibrary = Antilatency.StorageClient.Library.load();
        using var environmentSelectorLibrary = Antilatency.Alt.Environment.Selector.Library.load();
        using var trackingLibrary = Antilatency.Alt.Tracking.Library.load();
        using var network = CreateNetwork();

        #region CreateTrackingCotaskConstructor
        using var cotaskConstructor = trackingLibrary.createTrackingCotaskConstructor();
        #endregion

        using var environment = CreateEnvironment(storageClientLibrary, environmentSelectorLibrary);
        var placement = CreatePlacement(storageClientLibrary, trackingLibrary);

        Console.WriteLine("----- Settings -----");
        PrintEnvironmentMarkers(environment);
        PrintPlacementInfo(placement);
        using var udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        while (true)
        {
            Console.WriteLine("----- Waiting for a tracking node -----");
            var node = WaitForCompatibleNode(cotaskConstructor, network);
            var parent = network.nodeGetParent(node);
            string serialNo = network.nodeGetStringProperty(parent,
                    Antilatency.DeviceNetwork.Interop.Constants.HardwareSerialNumberKey);
            Console.WriteLine($"Tracking is about to start on node {node.value}, s/n of parent {serialNo}");
            //Make_Async_Print(node, cotaskConstructor, network, placement, environment);
            var Class = new Program();
            Class.Make_Async_Tracking(node, cotaskConstructor, network, placement, environment, udpSocket);
        }
    }
    private static async void Make_Async_Print(NodeHandle node, ITrackingCotaskConstructor cotaskConstructor, INetwork network, Antilatency.Math.floatP3Q placement, IEnvironment environment)
    {
        #region StartTrackingTask
        using var cotask = cotaskConstructor.startTask(network, node, environment);
        #endregion
        await PrintTrackingStateAsync(cotask, placement, node);
    }
    private async void Make_Async_Tracking(NodeHandle node,ITrackingCotaskConstructor cotaskConstructor, INetwork network, Antilatency.Math.floatP3Q placement, IEnvironment environment, Socket udpClient)
    {
        #region StartTrackingTask
        using var cotask = cotaskConstructor.startTask(network, node, environment);
        #endregion
        await StartRecordingAsync(network, cotask, node, udpClient);
    }
    private static INetwork CreateNetwork()
    {
        using var adnLibrary = Antilatency.DeviceNetwork.Library.load();
        if (adnLibrary == null)
        {
            throw new Exception("Failed to load AntilatencyDeviceNetwork library");
        }

        Console.WriteLine(
            $"AntilatencyDeviceNetwork version: {adnLibrary.getVersion()}");

        adnLibrary.setLogLevel(Antilatency.DeviceNetwork.LogLevel.Info);

        using var filter = adnLibrary.createFilter();
        filter.addUsbDevice(Antilatency.DeviceNetwork.Constants.AllUsbDevices);

        return adnLibrary.createNetwork(filter);
    }

    private static IEnvironment CreateEnvironment(
            Antilatency.StorageClient.ILibrary storageClientLibrary,
            Antilatency.Alt.Environment.Selector.ILibrary environmentSelectorLibrary)
    {

        using var storage = storageClientLibrary.getLocalStorage();
        string environmentCode = storage.read("environment", "default");

        if (string.IsNullOrEmpty(environmentCode))
        {
            throw new Exception("Cannot create environment");
        }

        return environmentSelectorLibrary.createEnvironment(environmentCode);
    }

    private static Antilatency.Math.floatP3Q CreatePlacement(
            Antilatency.StorageClient.ILibrary storageClientLibrary,
            Antilatency.Alt.Tracking.ILibrary trackingLibrary)
    {

        using var storage = storageClientLibrary.getLocalStorage();
        string placementCode = storage.read("placement", "default");

        if (string.IsNullOrEmpty(placementCode))
        {
            var identityPlacement = new Antilatency.Math.floatP3Q();
            identityPlacement.rotation.w = 1;

            Console.WriteLine("Failed to get placement code, using identity placement");
            return identityPlacement;
        }

        return trackingLibrary.createPlacement(placementCode);
    }

    private static NodeHandle WaitForCompatibleNode(
            ITrackingCotaskConstructor cotaskConstructor,
            INetwork network)
    {

        uint prevUpdateId = 0;
        while (true)
        {
            var key = Console.ReadKey();
            if (key.KeyChar != ' ')
            { Console.WriteLine("Pressed"); Pressed_Button.Invoke(key.KeyChar); }
            uint updateId = network.getUpdateId();            

            if (updateId == prevUpdateId)
            {
                Thread.Yield();
                continue;
            }

            Console.WriteLine($"Network ID changed: {prevUpdateId} -> {updateId}");

            var node = cotaskConstructor
                .findSupportedNodes(network)
                .FirstOrDefault(
                    n => network.nodeGetStatus(n) ==
                        NodeStatus.Idle);

            if (node != NodeHandle.Null)
            {
                return node;
            }

            prevUpdateId = updateId;
        }
    }
    private static async Task PrintTrackingStateAsync(
            Antilatency.Alt.Tracking.ITrackingCotask cotask,
            Antilatency.Math.floatP3Q placement, NodeHandle node)
    {
        await Task.Run(() => PrintTrackingState(cotask, placement, node));
    }
    private static void PrintTrackingState(
            Antilatency.Alt.Tracking.ITrackingCotask cotask,
            Antilatency.Math.floatP3Q placement, NodeHandle node)
    {

        while (!cotask.isTaskFinished())
        {

            //var state = cotask.getExtrapolatedState(placement, 0.06f);
            var state = cotask.getState(Antilatency.Alt.Tracking.Constants.DefaultAngularVelocityAvgTime);


            if (state.stability.stage ==
                        Antilatency.Alt.Tracking.Stage.InertialDataInitialization)
            {
                continue;
            }

            Console.WriteLine(
                "{0,-12},{9} : {8} : {1,-11:G5} {2,-11:G5} {3,-11:G5} : " +
                "{4,-11:G4} {5,-11:G5} {6,-11:G5} {7:G5}",
                state.stability.stage,
                state.pose.position.x,
                state.pose.position.y,
                state.pose.position.z,
                state.pose.rotation.x,
                state.pose.rotation.y,
                state.pose.rotation.z,
                state.pose.rotation.w,
                state.stability.value,
                node.value.ToString());

            // Do not print too often; 5 FPS is enough.
            Thread.Sleep(200);
        }
    }

    private static void PrintEnvironmentMarkers(
            IEnvironment environment)
    {

        var markers = environment.getMarkers();

        Console.WriteLine("Environment markers:");
        for (var i = 0; i < markers.Length; ++i)
        {
            Console.WriteLine("    marker {0,-15} : {1,-12:G5} {2,-12:G5} {3,-12:G5}",
                i, markers[i].x, markers[i].y, markers[i].z);
        }

        Console.WriteLine();
    }

    private static void PrintPlacementInfo(Antilatency.Math.floatP3Q placement)
    {
        Console.WriteLine("Placement:");
        Console.WriteLine("    offset: {0:G5} {1:G5} {2:G5}",
            placement.position.x,
            placement.position.y,
            placement.position.z);

        Console.WriteLine("    rotation: {0:G5} {1:G5} {2:G5} {3:G5}",
            placement.rotation.x,
            placement.rotation.y,
            placement.rotation.z,
            placement.rotation.w);

        Console.WriteLine();
    }
    private async Task StartRecordingAsync(
            INetwork network,
        Antilatency.Alt.Tracking.ITrackingCotask altTrackingCotask, NodeHandle trackingNode, Socket udpClient)
    {
        await Task.Run(() => Start_Recording(network, altTrackingCotask, trackingNode,udpClient));
    }
    private void Start_Recording(INetwork network,
        ITrackingCotask altTrackingCotask, NodeHandle trackingNode, Socket udpClient)
    {
        if (trackingNode != NodeHandle.Null)
        {
            var parent = network.nodeGetParent(trackingNode);
            var hardwareSerialNo = network.nodeGetStringProperty(parent, Antilatency.DeviceNetwork.Interop.Constants.HardwareSerialNumberKey);
            string Tag = correct_names[hardwareSerialNo];

            full_path = Root_path + $"Tag_{Tag}_in_date_{date}.csv";
            if(Painting_Mode != "Paint")
            {
                using (StreamWriter sw = File.AppendText(full_path))
                {
                    sw.WriteLine(Columns);
                    sw.Close();
                }
            }
            EndPoint remotePoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234);
            if (altTrackingCotask != null)
            {
                while (!altTrackingCotask.isTaskFinished())
                {
                    State state = altTrackingCotask.getState(Antilatency.Alt.Tracking.Constants.DefaultAngularVelocityAvgTime);
                    string timestamp = DateTime.Now.ToString("HH:mm:ss.fffff");
                    if (state.stability.stage ==
                        Stage.InertialDataInitialization)
                    {
                        continue;
                    }
                    string data = "";
                    byte[] New_data = new byte[512];
                    if (Button_state != 'g')
                    {
                        if(Painting_Mode != "Paint") { data = $"PRESSED_BUTTON;{timestamp};{Button_state};{(-1, -1, -1, -1)};{(0, 0, 0)};{(-1, -1, -1)};{(-1, -1, -1)};NoStage;{-1}"; }
                        if(Painting_Mode != "Write")
                        {
                            Antilatency_Data msg = new()
                            {
                                mode = "PRESSED_BUTTON",
                                Timestamp = timestamp,
                                SocketTag = Button_state.ToString(),
                                Rotation_x = 0,
                                Rotation_y = 0,
                                Rotation_w = 0,
                                Rotation_z = 0,
                                Position_x = 0,
                                Position_y = 0,
                                Position_z = 0,
                                AngVelocity_x = 0,
                                AngVelocity_y = 0,
                                AngVelocity_z = 0,
                                Velocity_x = 0,
                                Velocity_y = 0,
                                Velocity_z = 0,
                                value = 0,
                                stage = "No Stage",
                            };
                            New_data = Encoding.ASCII.GetBytes(JsonSerializer.Serialize(msg));
                        }
                    }
                    else
                    {
                        var rotation = state.pose.rotation;
                        var position = state.pose.position;
                        var AngVelocity = state.localAngularVelocity;
                        var Velocity = state.velocity;
                        var Stage = state.stability.stage;
                        var Value = state.stability.value;
                        if (Painting_Mode != "Paint")
                        {
                            data = $"{Mode};{timestamp};{Tag};{(rotation.x, rotation.y, rotation.z, rotation.w)};{(position.x, position.y, position.z)};{(AngVelocity.x, AngVelocity.y, AngVelocity.z)};{(Velocity.x, Velocity.y, Velocity.z)};{Stage};{Value}";

                        }
                        if(Painting_Mode != "Write")
                        {
                            Antilatency_Data msg = new()
                            {
                                mode = Mode,
                                Timestamp = timestamp,
                                SocketTag = Tag,
                                Rotation_x = rotation.x,
                                Rotation_y = rotation.y,
                                Rotation_w = rotation.w,
                                Rotation_z = rotation.z,
                                Position_x = position.x,
                                Position_y = position.y,
                                Position_z = position.z,
                                AngVelocity_x = AngVelocity.x,
                                AngVelocity_y = AngVelocity.y,
                                AngVelocity_z = AngVelocity.z,
                                Velocity_x = Velocity.x,
                                Velocity_y = Velocity.y,
                                Velocity_z = Velocity.z,
                                value = Value,
                                stage = Stage.ToString(),
                            };
                            New_data = Encoding.ASCII.GetBytes(JsonSerializer.Serialize(msg));
                        }
                    }
                    if(Painting_Mode != "Write")
                    {
                        udpClient.SendTo(New_data, remotePoint);
                    }

                    if(Painting_Mode != "Paint")
                    {
                        using (StreamWriter sw = File.AppendText(full_path))
                        {
                            sw.WriteLine(data);
                            sw.Close();
                        }
                    }

                    if (Mode == "maxHzSleep0") { if (Painting_Mode == "Both") Thread.Sleep(1); else Thread.Sleep(0); }
                            if (Mode == "Sleep1") Thread.Sleep(1);
                }
            }
            else
            {
                Console.WriteLine("Failed to start tracking task on node");
            }
        }
    }

    private static void Write_With_Button(char message)
    {
        Button_state = message;
        var path = Root_path + $"Button_{date}.csv";
        if (Painting_Mode != "Paint")
        {
            using (StreamWriter sw = File.AppendText(path))
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fffff");
                sw.WriteLine($"Pressed_Button;{message};{timestamp}");
                sw.Close();
            }
        }
    }

    private static string Make_Full_Path(string Tag, string date)
    {
        string directory;
        using(StreamReader sr = new(Player_Config_Path))
        {
            string path = sr.ReadToEnd();
            string[] lines = path.Split('\n');
            string Name = lines[1];
            string Play_Mode = lines[3];
            string Comment = lines[5];
            directory =  Root_path + @$"{Name}\{Play_Mode}\{Comment}";

        }
        Directory.CreateDirectory(directory);
        string result =  $@"{directory}\Tag_{Tag}_date_{date}.csv";
        return result;
    }


}
class Antilatency_Data
{
    public string mode { get; set; }
    public string Timestamp { get; set; }
    public string SocketTag { get; set; }
    public float Rotation_w { get; set; }
    public float Rotation_x { get; set; }
    public float Rotation_y { get; set; }
    public float Rotation_z { get; set; }
    public float Position_x { get; set; }
    public float Position_y { get; set; }
    public float Position_z { get; set; }
    public float AngVelocity_x { get; set; }
    public float AngVelocity_y { get; set; }
    public float AngVelocity_z { get; set; }
    public float Velocity_x { get; set; }
    public float Velocity_y { get; set; }
    public float Velocity_z { get; set; }
    public string stage { get; set; }
    public float value { get; set; }


}

