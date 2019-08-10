using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using VRageMath;
using VRage.Game;
using Sandbox.ModAPI.Interfaces;
using Sandbox.ModAPI.Ingame;
using Sandbox.Game.EntityComponents;
using VRage.Game.Components;
using VRage.Collections;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Game.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;

namespace CDS
{
    public sealed class Program : MyGridProgram
    {  
        /*
  Properties must be written like that
  DoomStation:
  1.5:2.5:2.5:
  2.5d:5.0d:10.0d:
  25.0d:45.0d:
  
  DoomStation:1.5:2.5:2.5:2.5d:5.0d:10.0d:25.0d:45.0d:
  */

        public Program()
        { Runtime.UpdateFrequency = UpdateFrequency.Update100; }

        //v 1.00
        class Drone
        {
            private Program program;

            private IMyTextPanel statusPanel;
            private IMyTextPanel routePanel;
            private List<IMyCargoContainer> containers;

            private string[] DroneSystemNames = { "Route", "Status", "[ACDS]" }; // last one is for all container blocks
            private int bayNumber;

            public IMyTextPanel getStatusPanel() { return statusPanel; }
            public IMyTextPanel getRoutePanel() { return routePanel; }
            public List<IMyCargoContainer> getCargoContainers() { return containers; }
            public int getBayNumber() { return bayNumber; }

            public bool Check()
            {
                if (statusPanel == null || routePanel == null || bayNumber == -1)
                    return false;
                else return true;
            }

            public Drone(Program newProgram)
            {
                program = newProgram;
                routePanel = null;
                statusPanel = null;
                containers = new List<IMyCargoContainer>();
                bayNumber = -1;
            }

            public void TryGetFromBay(int number, IMyShipConnector bayConnector)
            {
                routePanel = null;
                statusPanel = null;
                containers = new List<IMyCargoContainer>();
                bayNumber = -1;

                List<IMyTerminalBlock> routePanels = new List<IMyTerminalBlock>();
                program.GridTerminalSystem.SearchBlocksOfName(DroneSystemNames[0], routePanels);

                List<IMyTerminalBlock> statusPanels = new List<IMyTerminalBlock>();
                program.GridTerminalSystem.SearchBlocksOfName(DroneSystemNames[1], statusPanels);

                List<IMyTerminalBlock> cargoContainers = new List<IMyTerminalBlock>();
                program.GridTerminalSystem.SearchBlocksOfName(DroneSystemNames[2], cargoContainers);

                IMyShipConnector droneConnector = bayConnector.OtherConnector;

                foreach (IMyTerminalBlock _routePanel in routePanels)
                    if (_routePanel.IsSameConstructAs(droneConnector))
                        routePanel = _routePanel as IMyTextPanel;

                foreach (IMyTerminalBlock _statusPanel in statusPanels)
                    if (_statusPanel.IsSameConstructAs(droneConnector))
                        statusPanel = _statusPanel as IMyTextPanel;

                foreach (IMyTerminalBlock _cargoContainer in cargoContainers)
                    if (_cargoContainer.IsSameConstructAs(droneConnector))
                        containers.Add(_cargoContainer as IMyCargoContainer);

                if (statusPanel != null & routePanel != null)
                    bayNumber = number;

                statusPanels.Clear();
                routePanels.Clear();
                cargoContainers.Clear();
            }
        }

        class ACDS
        {

            //Fields
            private double approachDistance1, approachDistance2, approachDistance3;
            private double approachSpeed1, approachSpeed2, approachSpeed3;
            private double distanceToFirstNode, distanceToSecondNode;
            private string currentProperties;

            private Program program;

            private string myName;
            private int reset;
            private StringBuilder processingLog;
            private string previousMessage;

            private List<string[]> commands;
            private string unloadingCrateName = "[ACDS Holds]";
            private string[] systemPanelNames = {
    "Panel[Debug]",
    "Panel[Interface]",
    "Panel[Routes]",
    "Panel[Status]",
    "Panel[Names]",
    "Panel[Matrix]",
    "Panel[Table]"};
            private IMyTextPanel[] systemPanels;
            private List<IMyTerminalBlock> dataPanels;

            private List<IMyShipConnector> bayConnectors;
            /// <summary>
            /// 0 - Waiting.
            /// 1 - Hub: Empty after sending to bay, Bay: waitings for new arrival.
            /// 2 - Departing
            /// </summary>
            private List<int> bayStatus;
            private int hubIsOccupied;

            private Drone drone;
            private List<string> destinationNames;
            private void ShowError(string message)
            {
                if (systemPanels[0] != null)
                    processingLog.Append(message);
                else program.Echo(message);
            }

            private IMyTerminalBlock GetFirstWithName(string name)
            {
                IMyTerminalBlock block = null;
                List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
                program.GridTerminalSystem.SearchBlocksOfName(name, blocks);
                if (blocks.Count > 0) block = blocks[0];
                else processingLog.Append(" - Unable to find block with name:\n = " + name + "\n");
                return block;
            }

            private List<IMyTerminalBlock> GetBlocksWithName(List<IMyTerminalBlock> inputBlockList, string name)
            {
                List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
                program.GridTerminalSystem.SearchBlocksOfName(name, blocks);
                foreach (IMyTerminalBlock block in blocks)
                    if (block.IsSameConstructAs(program.Me))
                        inputBlockList.Add(block);
                return inputBlockList;
            }

            private List<IMyTerminalBlock> SortListByDistanceFrom(List<IMyTerminalBlock> blocks, IMyTerminalBlock previousBlock)
            {
                if (blocks.Count < 2)
                    return blocks;
                double distance = Math.Round((blocks[0].GetPosition() - previousBlock.GetPosition()).Length(), 2);
                IMyTerminalBlock closesBlock = blocks[0];
                int index = 0, closestIndex = 0;
                foreach (IMyTerminalBlock block in blocks)
                {
                    double currentDistance = Math.Round((previousBlock.GetPosition() - block.GetPosition()).Length(), 2);
                    if (currentDistance <= distance)
                    {
                        distance = currentDistance;
                        closesBlock = block;
                        closestIndex = index;
                    }
                    index++;
                }

                blocks.RemoveAt(closestIndex);
                List<IMyTerminalBlock> outList = new List<IMyTerminalBlock>();
                outList.Add(closesBlock);

                if (blocks.Count > 1)
                {
                    blocks = SortListByDistanceFrom(blocks, closesBlock);
                    foreach (IMyTerminalBlock block in blocks)
                        outList.Add(block);
                    return outList;
                }
                else
                {
                    outList.Add(blocks[0]);
                    return outList;
                }
            }

            private string InitializeDataPanels()
            {
                string dataPanelsInitializationLog = "";
                dataPanels = new List<IMyTerminalBlock>();
                List<IMyTerminalBlock> textPanels = new List<IMyTerminalBlock>();
                List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
                program.GridTerminalSystem.GetBlocksOfType<IMyTextPanel>(blocks);
                Vector3D myPos = program.Me.GetPosition();

                if (blocks.Count != 0)
                    // check all panels to match our conditions.
                    for (int count = 0, i = 1; count < blocks.Count; count++)
                    {
                        bool pass = false;
                        // check if this panel was system
                        for (int s = 0; s < systemPanelNames.Length && !pass; s++)
                        {
                            if (blocks[count].CustomName == systemPanelNames[s])
                            {
                                systemPanels[s] = blocks[count] as IMyTextPanel;
                                pass = true;
                            }
                        } // end for
                          // check if this panel is in 2m range from programm block
                        if ((myPos - blocks[count].GetPosition()).Length() < 2 && !pass)
                        {
                            if (i < 10)
                                blocks[count].CustomName = "DataCenter 0" + i;
                            else
                                blocks[count].CustomName = "DataCenter " + i;
                            dataPanels.Add(blocks[count]);
                            i++;
                        }
                    } // end for

                // data panels not found
                if (dataPanels.Count == 0)
                {
                    reset = 1;
                    return dataPanelsInitializationLog += " - Can't find text panels in 2m radius.\n";
                }

                // if there enough panels and system panels not found, rename and add free panels.
                for (int i = 0; i < systemPanels.Length; i++)
                {
                    if (systemPanels[i] == null)
                    {
                        systemPanels[i] = dataPanels[i] as IMyTextPanel;
                        systemPanels[i].CustomName = systemPanelNames[i];
                        systemPanels[i].ContentType = ContentType.TEXT_AND_IMAGE;
                    }
                    systemPanels[i].ShowInTerminal = true;
                    systemPanels[i].ShowInToolbarConfig = false;
                }
                string message = " - Text panels initialized = " + dataPanels.Count + "\n";
                dataPanelsInitializationLog = message;
                if (dataPanels.Count < 9)
                {
                    reset = 1;
                    return dataPanelsInitializationLog += " - Not enough text panels.\n";
                }

                foreach (IMyTextPanel dataPanel in dataPanels)
                {
                    string text = dataPanel.GetText();
                    if ((text.Length > 0 && text[0] != 'R') || text.Length == 0)
                    {
                        dataPanel.WriteText("ROUTETO:", false);
                        dataPanel.ShowInTerminal = false;
                        dataPanel.ShowInToolbarConfig = false;
                    }
                } // end foreach
                return dataPanelsInitializationLog;
            }

            private string InitializeConnectors()
            {
                bayConnectors = new List<IMyShipConnector>();
                List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
                blocks = GetBlocksWithName(blocks, "HubBay");
                blocks = GetBlocksWithName(blocks, "H#");

                // initialize connectors
                if (blocks.Count == 0)
                {
                    reset = 1;
                    return " - FATAL\n - There must be at least one bay connector\nBuild a connector and name it [H#]\n";
                }
                else
                {
                    bayConnectors.Add(blocks[0] as IMyShipConnector);
                    bayConnectors[0].CustomName = "HubBay";
                    hubIsOccupied = 0;
                    blocks.Clear();
                    blocks = GetBlocksWithName(blocks, "D#");
                    blocks = GetBlocksWithName(blocks, "Drone Bay");

                    if (blocks.Count > 0)
                    {
                        blocks = SortListByDistanceFrom(blocks, bayConnectors[0]);
                        int count1 = 1;
                        foreach (IMyTerminalBlock connector in blocks)
                        {
                            connector.CustomName = "Drone Bay " + count1;
                            bayConnectors.Add(connector as IMyShipConnector);
                            count1++;
                        }//end foreach
                    }

                    blocks.Clear();
                    if (bayConnectors == null)
                    {
                        reset = 1;
                        return " - There is no one drone bays connector \n - Find one connector and name it [H#]\n";
                    }
                } // end else
                return " - Drone bay initialized = " + bayConnectors.Count + "\n";
            }

            private string WriteDockingRoutes()
            {
                if (systemPanels[2] == null)
                {
                    reset = 1;
                    return " - Bay routes panel not found\n";
                }
                if (bayConnectors.Count == 0)
                {
                    reset = 1;
                    return " - Bay connectors not found\n";
                }
                int count = 0;

                StringBuilder data = new StringBuilder("<><>Working Bays<><>\n@");

                foreach (IMyTerminalBlock connector in bayConnectors)
                {
                    if (connector != null && connector.IsFunctional)
                    {
                        IMyEntity myEntity = bayConnectors[count] as IMyEntity;
                        Vector3D orientation = new Vector3D(
                        myEntity.WorldMatrix.M31 * -1,
                        myEntity.WorldMatrix.M32 * -1,
                        myEntity.WorldMatrix.M33 * -1);
                        Vector3D position = bayConnectors[count].GetPosition();
                        data.Append("Route:" + (count) + ":\n");
                        data.Append(approachSpeed1 + ":" + approachDistance1 + ":2:Bay " + (count) + ":");
                        data.Append(Math.Round(position.X, 2) + ":");
                        data.Append(Math.Round(position.Y, 2) + ":");
                        data.Append(Math.Round(position.Z, 2) + ":\n");
                        data.Append(approachSpeed2 + ":" + approachDistance2 + ":2:Entrance " + (count) + ":");
                        data.Append(Math.Round(position.X + orientation.X * distanceToFirstNode, 2) + ":");
                        data.Append(Math.Round(position.Y + orientation.Y * distanceToFirstNode, 2) + ":");
                        data.Append(Math.Round(position.Z + orientation.Z * distanceToFirstNode, 2) + ":\n");
                        data.Append(approachSpeed3 + ":" + approachDistance3 + ":2:Way " + (count) + ":");
                        data.Append(Math.Round(position.X + orientation.X * distanceToSecondNode, 2) + ":");
                        data.Append(Math.Round(position.Y + orientation.Y * distanceToSecondNode, 2) + ":");
                        data.Append(Math.Round(position.Z + orientation.Z * distanceToSecondNode, 2) + ":\n");
                    }
                    count++;
                } //end foreach

                systemPanels[2].WriteText(data.ToString(), false);
                return "";
            }

            private string SetupDockingBays()
            {
                string setupLog = InitializeConnectors();

                bayStatus = new List<int>();
                if (bayConnectors.Count > 0)
                    foreach (IMyTerminalBlock connector in bayConnectors)
                        bayStatus.Add(0);

                return setupLog;
            }

            public ACDS(Program nProgram)
            {
                program = nProgram;
                drone = new Drone(program);
                myName = "";
                systemPanels = new IMyTextPanel[7];
                commands = new List<string[]>();
                currentProperties = "";
            }

            private void ReplaceMyName(string newName)
            {
                if (systemPanels[4] != null && systemPanels[4].GetText() == "")
                {
                    systemPanels[4].WriteText(myName, false);
                }
                else
                {
                    List<string> names = ReadDestinationNames();
                    systemPanels[4].WriteText("", false);
                    for (int i = 0; i < names.Count; i++)
                    {
                        if (names[i] == myName)
                            names[i] = newName;
                        systemPanels[4].WriteText(names[i] + " ", true);
                    }
                    ShowError(" - The name of this station has changed.\n" +
                      " - Do not forget to inform other stations about\n   the change.\n");
                }
            }

            private void InitializeProperties()
            {
                if (program.Me.CustomData != "")
                {
                    currentProperties = program.Me.CustomData;
                    string[] words = currentProperties.Split(':');
                    if (words.Length < 9)
                        program.Echo("\n\n\nWrite this station name and other properties" +
                        " in this programmable block custom data.\n\n\n");
                    if (myName != "" && myName != words[0])
                        ReplaceMyName(words[0]);
                    myName = words[0];

                    approachDistance1 = Convert.ToDouble(words[1]);
                    approachDistance2 = Convert.ToDouble(words[2]);
                    approachDistance3 = Convert.ToDouble(words[3]);
                    approachSpeed1 = Convert.ToDouble(words[4]);
                    approachSpeed2 = Convert.ToDouble(words[5]);
                    approachSpeed3 = Convert.ToDouble(words[6]);
                    distanceToFirstNode = Convert.ToDouble(words[7]);
                    distanceToSecondNode = Convert.ToDouble(words[8]);
                }
            }

            public int Initialize()
            {
                reset = 0;
                InitializeProperties();
                processingLog = new StringBuilder("");
                processingLog.Append("   " + myName + " init time \n - " +
                  System.DateTime.Now.ToString() + "\n");
                processingLog.Append(InitializeDataPanels());
                processingLog.Append(SetupDockingBays());

                WriteDockingRoutes();

                previousMessage = processingLog.ToString();
                if (systemPanels[0] != null)
                    systemPanels[0].WriteText(processingLog.ToString(), false);
                return reset;
            }

            private void BlocksCheck<T>(List<T> blocks) where T : IMyTerminalBlock
            {
                if (blocks.Count != 0)
                {
                    int damagedBlocks = 0, workingBlocks = 0, destroyedBlocks = 0;

                    foreach (IMyTerminalBlock block in blocks)
                    {
                        if (block == null)
                            destroyedBlocks++;
                        else
                        {
                            if (!block.IsFunctional)
                                damagedBlocks++;
                            else workingBlocks++;
                        }
                    } // end foreach
                    if (workingBlocks != 0)
                        systemPanels[3].WriteText(" - online: " + workingBlocks + "\n", true);
                    if (damagedBlocks != 0)
                        systemPanels[3].WriteText(" - damaged: " + damagedBlocks + "\n", true);
                    if (destroyedBlocks != 0)
                        systemPanels[3].WriteText(" - errors: " + destroyedBlocks + "\n", true);
                }
            }

            private string[] ReadDroneData(IMyTextPanel Panel)
            {
                string[] dataWords = new string[] { "", "", "" };

                var text = Panel.GetText();

                string[] lines = text.Split('\n');

                for (int l = 0; l < lines.Length; l++)
                {
                    string[] wordPair = lines[l].Split(':');
                    try
                    {
                        switch (wordPair[0].ToUpper())
                        {
                            case "SENDER":
                                dataWords[0] = wordPair[1];
                                break;

                            case "RECIPIENT":
                                dataWords[1] = wordPair[1];
                                break;

                            case "STATE":
                                dataWords[2] = wordPair[1];
                                break;

                            default:
                                if (dataWords[0] != "" && dataWords[1] != "" && dataWords[2] != "")
                                    return dataWords;
                                break;
                        }// end switch
                    }
                    catch
                    (Exception e)
                    {
                        processingLog.Append(" - Error occured during drone data reading\n");
                        processingLog.Append(e.ToString() + "\n");
                        return new string[0];
                    }
                }//end for

                if (dataWords[0] == "" || dataWords[1] == "" || dataWords[2] == "")
                    processingLog.Append(" - Error occured during drone data reading\n");
                return dataWords;
            }

            private string GetInnerRouteOld(int index, int state)
            {
                // state : 0 - out route, 1 - inner route.
                string data = "";
                int start = 0, end = 0, count = -1;
                string text = systemPanels[2].GetText();
                string pass = "@Route:" + index + ":";
                //                                                         to do
                while (count < index)
                {
                    start = text.IndexOf('@', start) + pass.Length + 1;
                    count++;
                }

                if (state == 0)
                    end = text.IndexOf('@', start);
                if (state == 1)
                {
                    end = start;
                    for (int i = 0; i < 12; i++)
                        end = text.IndexOf(':', end + 1) + 1;
                }

                for (int i = start; i < end; i++)
                    data += text[i];
                return data;
            }

            private string GetInnerRoute(int index, int state)
            {
                StringBuilder data = new StringBuilder("");

                IMyEntity myEntity = bayConnectors[index] as IMyEntity;
                Vector3D orientation = new Vector3D(
                myEntity.WorldMatrix.M31 * -1,
                myEntity.WorldMatrix.M32 * -1,
                myEntity.WorldMatrix.M33 * -1);
                Vector3D position = bayConnectors[index].GetPosition();

                int direction = 2; // reverse
                if (state == 2)
                    direction = 1; // forward
                data.Append(approachSpeed1 + ":" + approachDistance1 + ":" + direction + ":Bay_" + (index) + ":");
                data.Append(Math.Round(position.X, 2) + ":");
                data.Append(Math.Round(position.Y, 2) + ":");
                data.Append(Math.Round(position.Z, 2) + ":\n");

                data.Append(approachSpeed2 + ":" + approachDistance2 + ":" + direction + ":Entrance_" + (index) + ":");
                data.Append(Math.Round(position.X + orientation.X * distanceToFirstNode, 2) + ":");
                data.Append(Math.Round(position.Y + orientation.Y * distanceToFirstNode, 2) + ":");
                data.Append(Math.Round(position.Z + orientation.Z * distanceToFirstNode, 2) + ":\n");

                if (state == 0)
                {
                    data.Append(approachSpeed3 + ":" + approachDistance3 + ":2:Way_" + (index) + ":");
                    data.Append(Math.Round(position.X + orientation.X * distanceToSecondNode, 2) + ":");
                    data.Append(Math.Round(position.Y + orientation.Y * distanceToSecondNode, 2) + ":");
                    data.Append(Math.Round(position.Z + orientation.Z * distanceToSecondNode, 2) + ":\n");
                }

                return data.ToString(); // take all
            }

            private string ReverseRoute(string data)
            {
                if (data != null)
                {
                    string[] routeData = data.Split('\n');
                    StringBuilder reversedData = new StringBuilder("");
                    for (int i = routeData.Length - 1; i >= 0; i--)
                    {
                        if (i != 0 && routeData[i].Length > 1)
                            reversedData.Append(routeData[i] + "\n");
                        else if (routeData[i].Length > 1)
                            reversedData.Append(routeData[i]);
                    }
                    return reversedData.ToString();
                }
                return "";
            }

            private int SendDroneToEmptyBay()
            {
                if (bayConnectors.Count > 1 && bayStatus[0] == 0)
                {
                    int index = 0;
                    //find an empty bay
                    for (int i = 1; i < bayConnectors.Count; i++)
                    {
                        IMyShipConnector connector = bayConnectors[i] as IMyShipConnector;
                        if (bayStatus[i] == 0 & connector.Status == MyShipConnectorStatus.Connected)
                        {
                            index = i;
                            bayStatus[i] = 1;
                            bayStatus[0] = 1;
                            break;
                        }
                    }
                    //there is no empty bays
                    if (index == 0)
                    {
                        return 0;
                        //                                                  to do maybe
                    }
                    StringBuilder routeData = new StringBuilder("");
                    routeData.Append(GetInnerRoute(0, 1));
                    // get inner route to bay (index)
                    routeData.Append(ReverseRoute(GetInnerRoute(index, 2)));
                    processingLog.Append("\n - sended to bay " + index);
                    //write all collected data from datapanel to drone routepanel
                    drone.getRoutePanel().WriteText(routeData, false);
                    drone.getStatusPanel().WriteText(
                    "NEWCOMMAND:undocking:" +
                    "NEWCOMMAND:readroute:" +
                    "NEWCOMMAND:basemove:" +
                    "NEWCOMMAND:docking:" +
                    "NEWCOMMAND:clearroute:", true);
                }
                else return 1;
                return 0;
            }

            private List<string> ReadDestinationNames()
            {
                string text = systemPanels[4].GetText().Trim(' ');

                List<string> names = new List<string>();

                string[] words = text.Split(' ');

                foreach (string word in words)
                    names.Add(word);

                return names;
            }

            private List<int[]> ReadRoutesTable(int size)
            {
                List<int[]> Table = new List<int[]>();

                string text = systemPanels[6].GetText();
                string[] lines = text.Split('\n');

                for (int l = 0; l < lines.Length; l++)
                {
                    string[] pair = lines[l].Split(':');

                    int A = 0, B = 0;

                    try
                    {
                        A = int.Parse(pair[0]);
                        B = int.Parse(pair[1]);
                    }
                    catch (Exception e)
                    {
                        program.Echo(e.ToString());

                        continue;
                    }

                    Table.Add(new int[] { A - 1, B - 1 });
                }
                return Table;
            }

            private int GetDestinationIndex(string destinationName)
            {
                int i = 0;
                while (i < destinationNames.Count)
                {
                    if (destinationNames[i].ToUpper() == destinationName.ToUpper())
                        return i;
                    i++;
                }
                if (i >= destinationNames.Count)
                    processingLog.Append(" - There is no such name <" + destinationName + "> in names list.\n");
                return -1;
            }

            private int[] SwapElements(int[] arr, int A, int B)
            {
                string temp = destinationNames[A];
                destinationNames[A] = destinationNames[B];
                destinationNames[B] = temp;
                arr[A] = arr[A] + arr[B];
                arr[B] = arr[A] - arr[B];
                arr[A] = arr[A] - arr[B];
                return arr;
            }

            private List<int[]> RebuildRouteTable(List<int[]> table, string destinationName)
            {
                int destionationsCount = destinationNames.Count;
                int[] destionationsNumbers = new int[destionationsCount];

                for (int i = 0; i < destionationsCount; i++)
                    destionationsNumbers[i] = i;

                int A = GetDestinationIndex(myName);
                int B = -1;

                if (destinationName != "none")
                    B = GetDestinationIndex(destinationName);
                else if (A == B) B--;

                if (A < 0 || B < 0)
                    return table;

                // to make calculation we need set A node as first and B as last.
                if (destionationsNumbers[0] == B)
                {
                    destionationsNumbers = SwapElements(destionationsNumbers, B, A);
                    B = A;
                }
                else
                    destionationsNumbers = SwapElements(destionationsNumbers, 0, A);

                if (destionationsNumbers[destionationsCount - 1] != B)
                    destionationsNumbers = SwapElements(destionationsNumbers, destionationsCount - 1, B);

                // dont touch that.... just work
                int[] DupTable = new int[destionationsCount];
                for (int i = 0; i < destionationsCount; i++)
                    DupTable[destionationsNumbers[i]] = i;
                // Rewrite route table
                foreach (int[] row in table)
                {
                    row[0] = DupTable[row[0]];
                    row[1] = DupTable[row[1]];
                }
                return table;
            }

            private int[,] BuildMatrix(List<int[]> table, int size)
            {
                int[,] matrix = new int[size, size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        matrix[x, y] = 0;
                foreach (int[] row in table)
                {
                    matrix[row[0], row[1]] = 1;
                    matrix[row[1], row[0]] = 1;
                }
                return matrix;
            }

            private void ShowMatrix(int[,] matrix)
            {
                StringBuilder data = new StringBuilder("");
                StringBuilder line = new StringBuilder("##");
                for (int x = 0; x < destinationNames.Count; x++)
                {
                    if (x < 10)
                        line.Append("|_" + (x + 1));
                    if (x >= 10)
                        line.Append("|" + (x + 1));
                }
                data.Append(line.Append('\n'));
                for (int y = 0; y < destinationNames.Count; y++)
                {
                    line.Clear();
                    if (y + 1 < 10)
                        line.Append("_" + (y + 1));
                    if (y + 1 >= 10)
                        line.Append("" + (y + 1));
                    for (int x = 0; x < destinationNames.Count; x++)
                        line.Append("|_" + matrix[x, y]);
                    data.Append(line.Append('\n'));
                }
                if (systemPanels[5] != null)
                    systemPanels[5].WriteText(data.ToString(), false);
                else reset = 1;
            }

            private int DijkstraPathFind(int[,] matrix, int myNumber, int size)
            {
                int[] distance = new int[size];
                bool[] visited = new bool[size];
                int[] parent = new int[size];

                for (int i = 0; i < size; i++)
                {
                    distance[i] = 255;
                    visited[i] = false;
                    parent[i] = -1;
                }

                distance[myNumber] = 0;
                int index = 0, u;
                for (int count = 0; count < size - 1; count++)
                {
                    int min = 255;
                    for (int i = 0; i < size - 1; i++)
                    {
                        if (!visited[i] && distance[i] <= min)
                        {
                            min = distance[i];
                            index = i;
                        }
                    }

                    u = index;
                    visited[index] = true;

                    for (int i = 0; i < size; i++)
                    {
                        if (!visited[i] && matrix[u, i] != 0 && distance[u] != 255
                        && (distance[u] + matrix[u, i] < distance[i]))
                        {
                            distance[i] = distance[u] + matrix[u, i];
                            parent[i] = index;
                        }
                    }
                }

                // is destination reachable?
                if (distance[size - 1] != 255)
                {
                    int heir = size - 1;
                    while (parent[heir] != myNumber)
                        heir = parent[heir];
                    if (parent[size - 1] == myNumber)
                    {
                        processingLog.Append(" - Send to: " + size + " - " + destinationNames[size - 1] + "\n");
                        return size - 1;
                    }
                    else
                    {
                        processingLog.Append(" - Send to: " + size + " - " + destinationNames[size - 1] + "\n");
                        processingLog.Append(" - Through: " + (heir + 1) + " - " + destinationNames[heir] + "\n");
                        return heir;
                    }
                }
                processingLog.Append(" - Destionation unreachable:\n - " + destinationNames[size - 1] + "\n");
                return -1;
            }

            //findRouteToDataPanel
            private IMyTextPanel FindRouteToDataPanel(string data)
            {
                if (dataPanels.Count != 0)
                    foreach (IMyTextPanel panel in dataPanels)
                    {
                        try
                        {
                            string[] lines = panel.GetText().Split('\n');

                            string[] words = lines[0].Split(':');

                            if (words[0].ToUpper() == "ROUTETO" && words[1].ToUpper() == data.ToUpper())
                                return panel;
                        }
                        catch (Exception e)
                        {
                        }
                    }
                processingLog.Append(" - Data panel: (" + data + ") - not found\n");
                return null;
            }

            // getRouteDataFromPanel
            private string GetRouteDataFromPanel(IMyTextPanel panel, string destination)
            {
                string data = "";
                if (panel != null)
                {
                    string text = panel.GetText();
                    string cutLine = "ROUTETO:" + destination + ":";
                    for (int i = cutLine.Length + 1; i < text.Length; i++)
                        data += text[i];
                }
                else
                {
                    processingLog.Append(" - Route to: " + destination + " - not found\n");
                    return "";
                }
                return data;
            }

            private bool IsDestionNamesContain(string destination)
            {
                foreach (string name in destinationNames)
                    if (name.ToUpper() == destination.ToUpper())
                        return true;
                return false;
            }

            private int SendToDestination(string destination, bool transit, bool deliver)
            {
                // Names writen in names table
                destinationNames = ReadDestinationNames();

                if (destinationNames.Count < 2)
                {
                    processingLog.Append(" - There is no known destinations\n");
                    return 2;
                }

                if (!IsDestionNamesContain(destination))
                {
                    processingLog.Append(" - Destination:" + destination + " - unknown.\n");
                    return 1;
                }

                //calculate trajectory
                List<int[]> table = ReadRoutesTable(destinationNames.Count);
                table = RebuildRouteTable(table, destination.ToUpper());
                int[,] matrix = BuildMatrix(table, destinationNames.Count);
                ShowMatrix(matrix);

                // find destination next node
                int destinationNameNumber = DijkstraPathFind(matrix, 0, destinationNames.Count);
                if (destinationNameNumber == -1)
                    return 1;

                // get route out from bay
                string routeData = "";
                routeData += GetInnerRoute(drone.getBayNumber(), 0);

                // get route to current destination
                IMyTextPanel panelWithRouteData = FindRouteToDataPanel(destinationNames[destinationNameNumber]);

                //processingLog .Append(" - Destinations list\n");
                //for (int i = 0; i < destinationNames.Count(); i++)
                //  processingLog .Append(" = " + (i+1) + " " + destinationNames[i] + "\n");

                string temp = GetRouteDataFromPanel(panelWithRouteData, destinationNames[destinationNameNumber]);
                if (temp == "") return 1;
                routeData += temp;
                bayStatus[drone.getBayNumber()] = 2;
                //write route data
                drone.getRoutePanel().WriteText(routeData, false);
                //write commands
                StringBuilder Command = new StringBuilder("");
                if (!transit)
                    Command.Append("NEWSENDER:" + myName + "\nNEWRECIPIENT:" + destination + "\n");
                Command.Append(
                "NEWSTATE:CLEARCOMM\n" +
                "NEWCOMMAND:UNDOCKING\n" +
                "NEWCOMMAND:READROUTE\n" +
                "NEWCOMMAND:MOVE\n" +
                "NEWCOMMAND:DOCKING\n" +
                "NEWCOMMAND:CLEARROUTE\n");
                if (deliver)
                    Command.Append("NEWCOMMAND:DELIVER\n");
                drone.getStatusPanel().WriteText(Command.ToString(), false);
                processingLog.Append(" - Drone sent to " + destination + "\n");
                // signal to hub that here is can be empty place even if it is hub
                hubIsOccupied = 2;
                return 0;
            }

            private int UnloadCargo()
            {
                IMyTerminalBlock BaseCrate = GetFirstWithName(unloadingCrateName);
                if (BaseCrate == null)
                {
                    processingLog.Append(" - Unable to unload drone holds.\n - Here is no specified crate.\n");
                    return 0;
                }

                var BaseHolds = BaseCrate.GetInventory(0);
                List<IMyCargoContainer> cargoContainers = drone.getCargoContainers();
                if (cargoContainers.Count == 0)
                {
                    processingLog.Append(" - Unable to unload drone holds.\n - Drone holds is not found.\n");
                    return 0;
                }

                processingLog.Append(" - Starting cargo transference.\n");
                for (int i = 0; i < cargoContainers.Count; i++)
                {
                    var containerInventory = cargoContainers[i].GetInventory(0);
                    List<MyInventoryItem> containerItems = new List<MyInventoryItem>();
                    containerInventory.GetItems(containerItems);
                    for (int j = containerItems.Count - 1; j >= 0; j--)
                        containerInventory.TransferItemTo(BaseHolds, j, null, true, null);
                }

                processingLog.Append(" - Cargo transference complete.\n");
                return 0;
            }

            private void NewPrivilegedCommand(string[] command)
            {
                if (commands.Count > 0)
                    commands.Insert(0, command);
                commands.Add(command);
            }

            private int DroneHandling(bool show)
            {
                // get drone that arrived to hub
                drone.TryGetFromBay(0, bayConnectors[0]);
                if (drone.Check())
                {
                    string[] statusWords = ReadDroneData(drone.getStatusPanel());

                    if (statusWords.Length == 0) // error oqqured in ReadDroneData()
                        return 0;

                    if (show)
                        processingLog.Append(" - Transport has arrived from:\n - <" + statusWords[0].ToUpper() + ">\n");

                    WriteDockingRoutes();

                    if (statusWords[1].ToUpper() == myName.ToUpper() || statusWords[1].ToUpper() == "NULL")
                    {
                        UnloadCargo();
                        if (statusWords[2].ToUpper() == "STANDBY" && bayConnectors.Count > 1)
                            commands.Add(new string[] { "PARK" });
                        
                        if (statusWords[2].ToUpper() == "DELIVER")
                        {
                            SendToDestination(statusWords[0].ToUpper(), false, false);
                            if (show)
                                processingLog.Append(" - Transport was sended back to:\n - " + statusWords[0].ToUpper() + "\n");
                        }
                    }

                    if (statusWords[1].ToUpper() != myName.ToUpper() && statusWords[1].ToUpper() != "NULL")
                    {
                        // idk this destination, send him back
                        if (!IsDestionNamesContain(statusWords[1]))
                        {
                            processingLog.Append(" - Destination:\n - " + statusWords[1] + " : unknown.\n");
                            processingLog.Append(" - Transport will be sended back to:\n - " + statusWords[0].ToUpper() + "\n");
                            NewPrivilegedCommand(new string[] { "SENDBACK", statusWords[0].ToUpper() });
                            return 1;
                        }
                        if (show)
                            processingLog.Append(" - Transit to " + statusWords[1].ToUpper() + "\n");
                        if (statusWords[2].ToUpper() == "STANDBY")
                            NewPrivilegedCommand(new string[] { "ROUTE", statusWords[0].ToUpper(), "TRANSIT" });
                        if (statusWords[2].ToUpper() == "DELIVER")
                            NewPrivilegedCommand(new string[] { "ROUTE", statusWords[0].ToUpper(), "DELIVER" });
                    }
                }
                return 0;
            }

            private void CheckBaysStatus()
            {
                if (bayConnectors.Count != 0)
                {
                    int index = 0;
                    StringBuilder sb = new StringBuilder(" - Drone bays status\n");
                    foreach (IMyShipConnector connector in bayConnectors)
                    {
                        if (connector != null) //check if connector not destroyed.
                        {
                            sb.Append(" = " + connector.CustomName + " : is ");

                            if (!connector.IsFunctional)
                            {
                                sb.Append("damaged!\n");
                                continue;
                            }

                            MyShipConnectorStatus status = connector.Status;

                            if (status == MyShipConnectorStatus.Connected)
                            {
                                // was sended from hub and docked to empty bay
                                if (bayStatus[index] == 1 && index != 0)
                                {
                                    bayStatus[index] = 0;
                                    bayStatus[0]--;
                                }
                                // get ready to roll away
                                if (bayStatus[index] == 2)
                                    sb.Append("ready to depart.\n");
                                else
                                    sb.Append("docked.\n");
                            }
                            if (status == MyShipConnectorStatus.Unconnected)
                            {
                                // waiting for drone sended from hub
                                if (bayStatus[index] == 1)
                                    sb.Append("new arrival.\n ");
                                else
                                    sb.Append("ready.\n");
                                // departed
                                if (bayStatus[index] == 2)
                                    bayStatus[index] = 0;

                            }
                        }
                        else
                            sb.Append(" = connector " + index + " : destroyed!\n");
                        index++;
                    }
                    systemPanels[3].WriteText(sb.ToString(), true);
                }
            }

            private void CheckForPropertiesChanges()
            {
                if (currentProperties != program.Me.CustomData)
                {
                    InitializeProperties();
                    WriteDockingRoutes();
                }
            }

            private void CheckSystems()
            {
                if (systemPanels[3] != null)
                {
                    systemPanels[3].WriteText(DateTime.Now.ToString(), false);
                    systemPanels[3].WriteText("\n<><><><> SYSTEM CHECK <><><><>\n", true);

                    systemPanels[3].WriteText(" - Data panels:\n", true);
                    if (dataPanels.Count != 0)
                        BlocksCheck(dataPanels);

                    systemPanels[3].WriteText(" - Drone bays :\n", true);
                    if (bayConnectors.Count != 0)
                    {
                        BlocksCheck(bayConnectors);
                        CheckBaysStatus();
                        IMyShipConnector hub = bayConnectors[0] as IMyShipConnector;
                        // transport has arrived
                        if (hub.Status == MyShipConnectorStatus.Connected && hubIsOccupied == 0
                        && bayStatus[0] <= 0 && bayConnectors.Count > 0)
                        {
                            DroneHandling(true);
                            hubIsOccupied = 1;
                        }
                        // check if transport is still in there when and other was sent
                        if (hub.Status == MyShipConnectorStatus.Connected && hubIsOccupied == 2
                        && bayStatus[0] <= 0 && bayConnectors.Count > 0)
                        {
                            DroneHandling(false);
                        }
                        // it's empty
                        if (hub.Status == MyShipConnectorStatus.Unconnected && (hubIsOccupied == 1 || hubIsOccupied == 2))
                            hubIsOccupied = 0;
                    }
                }
            }

            private void ShowLog()
            {
                if (systemPanels[0] != null)
                {
                    if (processingLog.Length != 0 & !Equals(previousMessage, processingLog.ToString()))
                    {
                        previousMessage = processingLog.ToString();
                        processingLog.Append(systemPanels[0].GetText());
                        processingLog.Insert(0, " ! " + System.DateTime.Now.ToString() + "\n");
                        systemPanels[0].WriteText(processingLog.ToString(), false);
                    }
                } else reset = 1;
            }

            private int MatrixMaintenance()
            {
                if (systemPanels[4] == null || systemPanels[5] == null || systemPanels[6] == null)
                    return 1;
                destinationNames = ReadDestinationNames();
                List<int[]> table = ReadRoutesTable(destinationNames.Count);

                table = RebuildRouteTable(table, "none");
                int[,] matrix = BuildMatrix(table, destinationNames.Count);
                ShowMatrix(matrix);
                return 0;
            }

            private void Commander()
            {
                if (commands.Count == 0)
                    return;
                string[] words = commands[0];
                string command = words[0].ToUpper();

                // one argument commands
                if (command == "RESET")
                { reset = 1; }
                if (command == "MATRIX")
                { MatrixMaintenance(); }
                if (command == "SHOW")
                {
                    command = words[1].ToUpper();
                    if (command == "DOCKING_ROUTES" || command == "DR")
                        SetupDockingBays();
                }
                if (command == "PARK")
                {
                    SendDroneToEmptyBay();
                }

                //more that one argument commands
                try
                {
                    if (command == "SENDTO" || command == "DELIVERTO") // work all possible bays
                    {
                        if (words[1] != "" && words[1].ToUpper() != myName.ToUpper())
                        {
                            if (bayConnectors.Count > 1)
                                for (int i = 1; i < bayConnectors.Count - 1; i++)
                                    drone.TryGetFromBay(i, bayConnectors[i]);
                            //TODO. can be separeted in different runs as progress bar. 
                            //because search can take a while 
                            else
                                drone.TryGetFromBay(0, bayConnectors[0]);

                            if (drone.Check())
                            {
                                if (words[0].ToUpper() == "SENDTO")
                                    SendToDestination(words[1], false, false);
                                else
                                    SendToDestination(words[1], false, true);
                            }
                            else processingLog.Append(" - There is no drones to send to:\n" + words[1] + "\n");
                        }
                    }
                    if (command == "ROUTE") // this command work with hub bay only
                    {
                        drone.TryGetFromBay(0, bayConnectors[0]);
                        if (drone.Check()) {
                            bool deliver = (words[2] == "DELIVER") ? true : false;
                            SendToDestination(words[1], true, deliver);
                        }
                        else processingLog.Append(" - Error in send back command:\n" + words[1] + "\n");
                    }
                    if (command == "SENDBACK") // this command work with hub bay only
                    {
                        drone.TryGetFromBay(0, bayConnectors[0]);
                        if (drone.Check())
                            SendToDestination(words[1], false, false);
                        else processingLog.Append(" - Error in send back command:\n" + words[1] + "\n");
                    }
                    if (command == "REVERSE")
                    {
                        IMyTextPanel panel = FindRouteToDataPanel(words[1]);
                        if (panel != null)
                        {
                            string data = GetRouteDataFromPanel(panel, words[1]);
                            data = ReverseRoute(data);
                            data = "$ROUTETO:" + words[1] + ":\n" + data;
                            panel.WriteText(data, false);
                        }
                    }
                }
                catch (Exception e)
                {
                    processingLog.Append(" - " + command + "\nError!:\n " + e.ToString() + "\n");
                }
                commands.RemoveAt(0);
            }

            private void ReadInput()
            {
                if (systemPanels[1] != null)
                {
                    // Read from
                    var text = systemPanels[1].GetText();
                    if (text != "[Waiting for input]\n")
                    {
                        string[] lines = text.Split('\n');
                        for (int l = 1; l < lines.Length; l++)
                            commands.Add(lines[l].Split(' '));

                        //TODO. Commands must be added to commands list 
                        //and then they must be invoked one by one at different BP runs

                        systemPanels[1].WriteText("[Waiting for input]\n", false);
                    }
                }
            }

            public int Run()
            {
                processingLog = new StringBuilder("");
                CheckForPropertiesChanges();
                CheckSystems();
                Commander();
                ReadInput();
                ShowLog();
                return reset;
            }
        }

        bool initialization = false;
        int reset = 1;

        ACDS unit;

        public void Main(string argument)
        {

            if (!initialization)
            {
                unit = new ACDS(this);
                initialization = true;
            }
            if (reset == 0) reset = unit.Run();
            else reset = unit.Initialize();
        }

    }
}
