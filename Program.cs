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
            string linuxVmName1 = Utilities.CreateRandomName("vm1-");
            string linuxVmName2 = Utilities.CreateRandomName("vm2-");
            string linuxVmName3 = Utilities.CreateRandomName("vm3-");
            string publicIpDnsLabel = Utilities.CreateRandomName("pip");

            try
            {
                //=============================================================
                // Create a Linux VM using an image from PIR (Platform Image Repository)

                Utilities.Log("Creating a Linux VM");

                var linuxVM = azure.VirtualMachines.Define(linuxVmName1)
                        .WithRegion(Region.USWest)
                        .WithNewResourceGroup(rgName)
                        .WithNewPrimaryNetwork("10.0.0.0/28")
                        .WithPrimaryPrivateIPAddressDynamic()
                        .WithNewPrimaryPublicIPAddress(publicIpDnsLabel)
                        .WithPopularLinuxImage(KnownLinuxVirtualMachineImage.UbuntuServer16_04_Lts)
                        .WithRootUsername(UserName)
                        .WithRootPassword(Password)
                        .WithUnmanagedDisks()
                        .WithSize(VirtualMachineSizeTypes.Parse("Standard_D2a_v4"))
                        .DefineNewExtension("CustomScriptForLinux")
                            .WithPublisher("Microsoft.OSTCExtensions")
                            .WithType("CustomScriptForLinux")
                            .WithVersion("1.4")
                            .WithMinorVersionAutoUpgrade()
                            .WithPublicSetting("fileUris", ApacheInstallScriptUris)
                            .WithPublicSetting("commandToExecute", ApacheInstallCommand)
                            .Attach()
                        .Create();

                Utilities.Log("Created a Linux VM: " + linuxVM.Id);
                Utilities.PrintVirtualMachine(linuxVM);

                // De-provision the virtual machine
                Utilities.DeprovisionAgentInLinuxVM(linuxVM.GetPrimaryPublicIPAddress().Fqdn, 22, UserName, Password);

                //=============================================================
                // Deallocate the virtual machine
                Utilities.Log("Deallocate VM: " + linuxVM.Id);

                linuxVM.Deallocate();

                Utilities.Log("Deallocated VM: " + linuxVM.Id + "; state = " + linuxVM.PowerState);

                //=============================================================
                // Generalize the virtual machine
                Utilities.Log("Generalize VM: " + linuxVM.Id);

                linuxVM.Generalize();

                Utilities.Log("Generalized VM: " + linuxVM.Id);

                //=============================================================
                // Capture the virtual machine to get a 'Generalized image' with Apache
                Utilities.Log("Capturing VM: " + linuxVM.Id);

                var capturedResultJson = linuxVM.Capture("capturedvhds", "img", true);

                Utilities.Log("Captured VM: " + linuxVM.Id);

                //=============================================================
                // Create a Linux VM using captured image (Generalized image)
                JObject o = JObject.Parse(capturedResultJson);
                JToken resourceToken = o.SelectToken("$.resources[?(@.properties.storageProfile.osDisk.image.uri != null)]");
                if (resourceToken == null)
                {
                    throw new Exception("Could not locate image uri under expected section in the capture result -" + capturedResultJson);
                }
                string capturedImageUri = (string)(resourceToken["properties"]["storageProfile"]["osDisk"]["image"]["uri"]);

                Utilities.Log("Creating a Linux VM using captured image - " + capturedImageUri);

                var linuxVM2 = azure.VirtualMachines.Define(linuxVmName2)
                        .WithRegion(Region.USWest)
                        .WithExistingResourceGroup(rgName)
                        .WithNewPrimaryNetwork("10.0.0.0/28")
                        .WithPrimaryPrivateIPAddressDynamic()
                        .WithoutPrimaryPublicIPAddress()
                        .WithStoredLinuxImage(capturedImageUri) // Note: A Generalized Image can also be an uploaded VHD prepared from an on-premise generalized VM.
                        .WithRootUsername(UserName)
                        .WithRootPassword(Password)
                        .WithSize(VirtualMachineSizeTypes.Parse("Standard_D2a_v4"))
                        .Create();

                Utilities.PrintVirtualMachine(linuxVM2);

                var specializedVhd = linuxVM2.OSUnmanagedDiskVhdUri;
                //=============================================================
                // Deleting the virtual machine
                Utilities.Log("Deleting VM: " + linuxVM2.Id);

                azure.VirtualMachines.DeleteById(linuxVM2.Id); // VM required to be deleted to be able to attach it's
                                                               // OS Disk VHD to another VM (Deallocate is not sufficient)

                Utilities.Log("Deleted VM");

                //=============================================================
                // Create a Linux VM using 'specialized VHD' of previous VM

                Utilities.Log("Creating a new Linux VM by attaching OS Disk vhd - "
                        + specializedVhd
                        + " of deleted VM");

                var linuxVM3 = azure.VirtualMachines.Define(linuxVmName3)
                        .WithRegion(Region.USWest)
                        .WithExistingResourceGroup(rgName)
                        .WithNewPrimaryNetwork("10.0.0.0/28")
                        .WithPrimaryPrivateIPAddressDynamic()
                        .WithoutPrimaryPublicIPAddress()
                        .WithSpecializedOSUnmanagedDisk(specializedVhd, OperatingSystemTypes.Linux) // New user credentials cannot be specified
                        .WithSize(VirtualMachineSizeTypes.Parse("Standard_D2a_v4"))         // when attaching a specialized VHD
                        .Create();

                Utilities.PrintVirtualMachine(linuxVM3);
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
