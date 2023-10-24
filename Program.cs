// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.Samples.Common;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager;
using System.Net;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Compute;
using System.Net.NetworkInformation;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace CreateVMsUsingCustomImageOrSpecializedVHD
{
    public class Program
    {
        private static ResourceIdentifier? _resourceGroupId = null;
        private static readonly string UserName = Utilities.CreateUsername();
        private static readonly string Password = Utilities.CreatePassword();
        private readonly static List<string> ApacheInstallScriptUris = new List<string>()
        {
            "https://raw.githubusercontent.com/Azure/azure-libraries-for-net/master/Samples/Asset/install_apache.sh"
        };
        private static readonly string ApacheInstallCommand = "bash install_apache.sh";
        /**
         * Azure Compute sample for managing virtual machines -
         *  - Create a virtual machine
         *  - Deallocate the virtual machine
         *  - Generalize the virtual machine
         *  - Capture the virtual machine to create a generalized image
         *  - Create a second virtual machine using the generalized image
         *  - Delete the second virtual machine
         *  - Create a new virtual machine by attaching OS disk of deleted VM to it.
         */
        public static async Task RunSample(ArmClient client)
        {
            string rgName = Utilities.CreateRandomName("ComputeSampleRG");
            string vnetName = Utilities.CreateRandomName("vnet");
            string nicName1 = Utilities.CreateRandomName("nic1-");
            string nicName2 = Utilities.CreateRandomName("nic2-");
            string linuxVmName1 = Utilities.CreateRandomName("vm1-");
            string linuxVmName2 = Utilities.CreateRandomName("vm2-");
            string linuxVmName3 = Utilities.CreateRandomName("vm3-");
            string publicIpDnsLabel = Utilities.CreateRandomName("pip");

            try
            {
                // Get default subscription
                SubscriptionResource subscription = await client.GetDefaultSubscriptionAsync();

                // Create a resource group in the EastUS region
                Utilities.Log($"Creating resource group...");
                ArmOperation<ResourceGroupResource> rgLro = await subscription.GetResourceGroups().CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(AzureLocation.EastUS));
                ResourceGroupResource resourceGroup = rgLro.Value;
                _resourceGroupId = resourceGroup.Id;
                Utilities.Log("Created a resource group with name: " + resourceGroup.Data.Name);

                //=============================================================
                // Create a Linux VM using an image from PIR (Platform Image Repository)

                // Pre-creating some resources that the VM depends on
                Utilities.Log("Pre-creating some resources that the VM depends on");

                // Creating a virtual network
                var vnet1 = await Utilities.CreateVirtualNetwork(resourceGroup, vnetName);

                // Creating public ip
                var pip = await Utilities.CreatePublicIP(resourceGroup, publicIpDnsLabel);

                // Creating network interface
                var nic = await Utilities.CreateNetworkInterface(resourceGroup, vnet1.Data.Subnets[0].Id, pip.Id, nicName1);

                Utilities.Log("Creating a Linux VM");

                VirtualMachineData linuxVMInput = new VirtualMachineData(resourceGroup.Data.Location)
                {
                    HardwareProfile = new VirtualMachineHardwareProfile()
                    {
                        VmSize = VirtualMachineSizeType.StandardF2
                    },
                    StorageProfile = new VirtualMachineStorageProfile()
                    {
                        ImageReference = new ImageReference()
                        {
                            Publisher = "Canonical",
                            Offer = "UbuntuServer",
                            Sku = "16.04-LTS",
                            Version = "latest",
                        },
                    },
                    OSProfile = new VirtualMachineOSProfile()
                    {
                        AdminUsername = UserName,
                        AdminPassword = Password,
                        ComputerName = linuxVmName1,
                    },
                    NetworkProfile = new VirtualMachineNetworkProfile()
                    {
                        NetworkInterfaces =
                        {
                            new VirtualMachineNetworkInterfaceReference()
                            {
                                Id = nic.Id,
                                Primary = true,
                            }
                        }
                    },
                };
                var linuxVmLro = await resourceGroup.GetVirtualMachines().CreateOrUpdateAsync(WaitUntil.Completed, linuxVmName1, linuxVMInput);
                VirtualMachineResource linuxVM = linuxVmLro.Value;

                Utilities.Log("Created a Linux VM: " + linuxVM.Id.Name);
                Utilities.PrintVirtualMachine(linuxVM);

                // Use a VM extension to install Apache Web servers
                // Definate vm extension input data
                Utilities.Log($"Use a VM extension to install Apache Web servers...");
                var extensionInput = new VirtualMachineExtensionData(resourceGroup.Data.Location)
                {
                    Publisher = "Microsoft.OSTCExtensions",
                    ExtensionType = "CustomScriptForLinux",
                    TypeHandlerVersion = "1.4",
                    AutoUpgradeMinorVersion = true,
                    EnableAutomaticUpgrade = false,
                    Settings = BinaryData.FromObjectAsJson(new
                    {
                        fileUris = ApacheInstallScriptUris,
                        commandToExecute = ApacheInstallCommand,
                    })
                };
                _ = await linuxVM.GetVirtualMachineExtensions().CreateOrUpdateAsync(WaitUntil.Completed, "CustomScriptForLinux", extensionInput);
                Utilities.Log("Extension installed");

                // De-provision the virtual machine

                pip = await resourceGroup.GetPublicIPAddresses().GetAsync(publicIpDnsLabel);
                string host =  pip.Data.IPAddress.ToString();
                Utilities.DeprovisionAgentInLinuxVM(host, 22, UserName, Password);

                //=============================================================
                // Deallocate the virtual machine
                Utilities.Log("Deallocate VM: " + linuxVM.Id.Name);

                await linuxVM.DeallocateAsync(WaitUntil.Completed);

                Utilities.Log("Deallocated VM: " + linuxVM.Id.Name);

                //=============================================================
                // Generalize the virtual machine
                Utilities.Log("Generalize VM: " + linuxVM.Id.Name);

                await linuxVM.GeneralizeAsync();

                Utilities.Log("Generalized VM: " + linuxVM.Id.Name);

                //=============================================================
                // Capture the virtual machine to get a 'Generalized image' with Apache
                Utilities.Log("Capturing VM: " + linuxVM.Id);

                //var capturedResultJson = linuxVM.Capture("capturedvhds", "img", true);

                VirtualMachineCaptureContent captureInput = new VirtualMachineCaptureContent("capturedvhds", "img", true);
                var capturedResultJson = await linuxVM.CaptureAsync(WaitUntil.Completed, captureInput);


                Utilities.Log("Captured VM: " + linuxVM.Id);

                //=============================================================
                // Create a Linux VM using captured image (Generalized image)

                //JObject o = JObject.Parse(capturedResultJson);
                //JToken resourceToken = o.SelectToken("$.resources[?(@.properties.storageProfile.osDisk.image.uri != null)]");
                //if (resourceToken == null)
                //{
                //    throw new Exception("Could not locate image uri under expected section in the capture result -" + capturedResultJson);
                //}
                //string capturedImageUri = (string)(resourceToken["properties"]["storageProfile"]["osDisk"]["image"]["uri"]);

                //Utilities.Log("Creating a Linux VM using captured image - " + capturedImageUri);

                //var linuxVM2 = azure.VirtualMachines.Define(linuxVmName2)
                //        .WithRegion(Region.USWest)
                //        .WithExistingResourceGroup(rgName)
                //        .WithNewPrimaryNetwork("10.0.0.0/28")
                //        .WithPrimaryPrivateIPAddressDynamic()
                //        .WithoutPrimaryPublicIPAddress()
                //        .WithStoredLinuxImage(capturedImageUri) 
                //        // Note: A Generalized Image can also be an uploaded VHD prepared from an on-premise generalized VM.
                //        .WithRootUsername(UserName)
                //        .WithRootPassword(Password)
                //        .WithSize(VirtualMachineSizeTypes.Parse("Standard_D2a_v4"))
                //        .Create();

                //Utilities.PrintVirtualMachine(linuxVM2);

                //var specializedVhd = linuxVM2.OSUnmanagedDiskVhdUri;
                ////=============================================================
                //// Deleting the virtual machine
                //Utilities.Log("Deleting VM: " + linuxVM2.Id);

                //azure.VirtualMachines.DeleteById(linuxVM2.Id); // VM required to be deleted to be able to attach it's
                //                                               // OS Disk VHD to another VM (Deallocate is not sufficient)

                //Utilities.Log("Deleted VM");

                ////=============================================================
                //// Create a Linux VM using 'specialized VHD' of previous VM

                //Utilities.Log("Creating a new Linux VM by attaching OS Disk vhd - "
                //        + specializedVhd
                //        + " of deleted VM");

                //var linuxVM3 = azure.VirtualMachines.Define(linuxVmName3)
                //        .WithRegion(Region.USWest)
                //        .WithExistingResourceGroup(rgName)
                //        .WithNewPrimaryNetwork("10.0.0.0/28")
                //        .WithPrimaryPrivateIPAddressDynamic()
                //        .WithoutPrimaryPublicIPAddress()
                //        .WithSpecializedOSUnmanagedDisk(specializedVhd, OperatingSystemTypes.Linux) // New user credentials cannot be specified
                //        .WithSize(VirtualMachineSizeTypes.Parse("Standard_D2a_v4"))         // when attaching a specialized VHD
                //        .Create();

                //Utilities.PrintVirtualMachine(linuxVM3);
            }
            finally
            {
                try
                {
                    if (_resourceGroupId is not null)
                    {
                        Utilities.Log($"Deleting Resource Group: {_resourceGroupId}");
                        await client.GetResourceGroupResource(_resourceGroupId).DeleteAsync(WaitUntil.Completed);
                        Utilities.Log($"Deleted Resource Group: {_resourceGroupId}");
                    }
                }
                catch
                {
                    Utilities.Log("Did not create any resources in Azure. No clean up is necessary");
                }
            }
        }

        public static async Task Main(string[] args)
        {
            try
            {
                //=================================================================
                // Authenticate
                var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
                var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
                var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
                var subscription = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
                ClientSecretCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                ArmClient client = new ArmClient(credential, subscription);

                await RunSample(client);
            }
            catch (Exception e)
            {
                Utilities.Log(e);
            }
        }
    }
}
