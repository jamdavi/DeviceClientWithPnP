using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml;

namespace DeviceClientWithPnP
{
    class Program
    {
        private const string thermostatComponentName = "thermostatComponent";
        private const string mainDeviceComponentName = "deviceConfig";
        private const string currentTempProperty = "currentTemp";
        private const string updateFirmwareCommandName = "updateFirmware";

        public static double MAXTEMP { get; private set; } = 33.5;
        public static double MINTEMP { get; private set; } = -15.0;

        static async Task Main(string[] args)
        {
            using var deviceClient = DeviceClient.CreateFromConnectionString("connstring", "deviceId", TransportType.Mqtt, new ClientOptions()
            {
                ModelId = "dtmi:something"
            });

            var twin = await deviceClient.GetTwinAsync();
            var lastReportedFirmware = twin.Properties.Reported["firm"] as string;

            // Get the latest firmware D2C message
            var firmwareCommand = new Command<ThermostatFirwareUpdateRequest, ThermostatFirmwareUpdateResponse>(
                new ThermostatFirwareUpdateRequest() { FirmwareVersion = Version.Parse(lastReportedFirmware) }, 
                null);

            var firmwareFromService = await deviceClient.SendCommandAsync(mainDeviceComponentName, updateFirmwareCommandName, firmwareCommand);

            if (firmwareFromService.ShouldUpdate)
            {
                UpdateFirmware(firmwareFromService.FirmwareBytes);
                await deviceClient.UpdatePropertyAsync(mainDeviceComponentName, "firm", firmwareFromService.FirmwareVersion.ToString());
            }

            // Set reboot command call back
            deviceClient.SetCommandCallback<ThermostatRebootRequest, ThermostatRebootResponse>(mainDeviceComponentName, "reboot", (component, commandName, rebootCommandObject) =>
            {
                var response = new ThermostatRebootResponse();
                if (rebootCommandObject.Request.WhenToReboot <= DateTime.Now)
                {
                    response.RebootStatus = "Device is rebooting now.";
                } else
                {
                    response.RebootStatus = $"Device is scheduled to reboot {rebootCommandObject.Request.WhenToReboot}.";
                }

                return response;
            });

            // Setup a loop to send the temperature every 5 seconds
            var t = Task.Run(async () =>
            {
                while (true)
                {
                    await deviceClient.SendTelemetryAsync(thermostatComponentName, currentTempProperty, GetCurrentTemperature());
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            });


            // Send some reported properties that are not part of the DTDL
            var tc = new TwinCollection();
            tc["something"] = "something else";
            tc["new"] = new ThermostatRebootResponse();
            //{  "new" : {
            //        "prop1" : "pro2"
            //    } 
            //}

            var prop = new Dictionary<string, Property<ISerializableSchema>>();
            prop["something"] = "something else";
            prop["new"] = new ThermostatFirmwareUpdateResponseJSON();
            prop["newbinary"] = new ThermostatFirmwareUpdateResponseBinary();
            //{  
            // "new"       "\{ \"new\" : \"value\" \}"
            // "newbinary" : "0x23313412451346143653465234621346235642345"
            //}

            await deviceClient.UpdateReportedPropertiesAsync(tc);

            // Send some device identifiers
            await deviceClient.UpdatePropertyAsync(mainDeviceComponentName, "serialNumber", "JAMESD1234");
            await deviceClient.UpdatePropertyAsync(mainDeviceComponentName, "hardwareVersion", "1.2.4");
            await deviceClient.UpdatePropertyAsync(mainDeviceComponentName, "numberOfSensors", 1);


            // Update the currentTemp property to something useful before reading from the device
            await deviceClient.UpdatePropertyAsync(thermostatComponentName, currentTempProperty, 24);
            // Read from the sensor
            await deviceClient.UpdatePropertyAsync(thermostatComponentName, currentTempProperty, GetCurrentTemperature());

            // A more complicated example of what we'd do when an external system wants to updte the currentTemp property
            // This example uses the WritableProperty object as the incoming object.
            deviceClient.SetWritablePropertyEvent(thermostatComponentName, currentTempProperty, propertyAction: (componentName, propertyName, incomingWritablePropertyObject) =>
            {
                var propertyTempValue = incomingWritablePropertyObject.Value.GetValue<double>();

                // Check to see if we're in range
                if (propertyTempValue > MAXTEMP || propertyTempValue < MINTEMP)
                {
                    // The value given was out of range. Get the current temp and report with a bad request
                    incomingWritablePropertyObject.Value = GetTheromstatTargetSetting();
                    incomingWritablePropertyObject.CreateBadResponse("Temperature is out of range");
                }
                else
                {
                    // The value is good so we can see what the thermostat is at
                    if (propertyTempValue > GetTheromstatTargetSetting() || propertyTempValue < GetTheromstatTargetSetting())
                    {
                        SetThermostat(propertyTempValue);
                        incomingWritablePropertyObject.CreateAcceptedResponse($"Setting temperature to {propertyTempValue}");
                    }
                    else
                    {
                        incomingWritablePropertyObject.CreateOKResponse($"Temperature is already at {propertyTempValue}");
                    }
                }
                deviceClient.UpdatePropertyAsync(componentName, propertyName, incomingWritablePropertyObject);

            });

            // A more complicated example of what we'd do when an external system wants to updte the currentTemp property
            // This example uses the TwinCollection object as the incoming object.
            deviceClient.SetWritablePropertyEventForComponent(thermostatComponentName, propertyActionAsTwinCollection: (componentName, incomingTwinCollection) =>
            {
                deviceClient.UpdatePropertiesAsync(componentName, );

            });
            deviceClient.SetWritablePropertyEvent(thermostatComponentName, currentTempProperty, propertyActionAsTwinCollection: (componentName, propertyName, incomingTwinCollection) =>
            {
                var propertyTempValue = incomingTwinCollection[currentTempProperty];
                var propertyVersion = incomingTwinCollection.Version;
                WritableProperty writableProperty = null;

                // Check to see if we're in range
                if (propertyTempValue > MAXTEMP || propertyTempValue < MINTEMP)
                {
                    // The value given was out of range. Get the current temp and report with a bad request
                    writableProperty = WritableProperty.CreateBadRequestResponse(GetTheromstatTargetSetting(), propertyVersion, "Temperature is out of range");
                }
                else
                {
                    // The value is good so we can see what the thermostat is at
                    if (propertyTempValue > GetTheromstatTargetSetting() || propertyTempValue < GetTheromstatTargetSetting())
                    {
                        SetThermostat(propertyTempValue);
                        writableProperty = WritableProperty.CreateAcceptedResponse(propertyTempValue, propertyVersion, $"Setting temperature to {propertyTempValue}");
                    }
                    else
                    {
                        writableProperty = WritableProperty.CreateOKResponse(propertyTempValue, propertyVersion, $"Temperature is already at {propertyTempValue}");
                    }
                }
                deviceClient.UpdatePropertyAsync(componentName, propertyName, writableProperty);

            });
        }

        public static double GetTheromstatTargetSetting()
        {
            return 22.0;
        }

        public static double GetCurrentTemperature()
        {
            return 22.0;
        }

        public static void SetThermostat(double value)
        {
            double bye = value;
        }

        public static void UpdateFirmware(byte[] value)
        {
            byte[] bye = value;
        }
    }

    public class ThermostatFirmwareUpdateResponseJSON : ISerializableSchema
    {
        public bool ShouldUpdate { get; set; }
        [JsonIgnore]
        public Version FirmwareVersion { get; set; }
        public byte[] FirmwareBytes { get; set; }

        public ThermostatFirmwareUpdateResponse Deserialize(string input)
        {
            throw new NotImplementedException();
        }

        public string Serialize()
        {
            return "james";
            throw new NotImplementedException();
        }
    }

    public class ThermostatFirmwareUpdateResponseBinary : ISerializableSchema
    {
        public bool ShouldUpdate { get; set; }
        [JsonIgnore]
        public Version FirmwareVersion { get; set; }
        public byte[] FirmwareBytes { get; set; }

        public ThermostatFirmwareUpdateResponse Deserialize(string input)
        {
            throw new NotImplementedException();
        }

        public string Serialize()
        {
            return "james";
            throw new NotImplementedException();
        }
    }

    public class ThermostatFirmwareUpdateResponse : ISerializableSchema<ThermostatFirmwareUpdateResponse>
    {
        public bool ShouldUpdate { get; set; }
        public Version FirmwareVersion { get; set; }
        public byte[] FirmwareBytes { get; set; }

        public ThermostatFirmwareUpdateResponse Deserialize(string input)
        {
            throw new NotImplementedException();
        }

        public string Serialize()
        {
            throw new NotImplementedException();
        }
    }

    public class ThermostatFirwareUpdateRequest : ISerializableSchema<ThermostatFirwareUpdateRequest>
    {
        public Version FirmwareVersion { get; set; }
        public ThermostatFirwareUpdateRequest Deserialize(string input)
        {
            throw new NotImplementedException();
        }

        public string Serialize()
        {
            throw new NotImplementedException();
        }
    }

    public class ThermostatRebootResponse : ISerializableSchema<ThermostatRebootResponse>
    {
        public string RebootStatus{ get; set; }
        public ThermostatRebootResponse Deserialize(string input)
        {
            throw new NotImplementedException();
        }

        public string Serialize()
        {
            throw new NotImplementedException();
        }
    }

    public class ThermostatRebootRequest : ISerializableSchema<ThermostatRebootRequest>
    {
        public DateTime WhenToReboot { get; set; }
        public ThermostatRebootRequest Deserialize(string input)
        {
            throw new NotImplementedException();
        }

        public string Serialize()
        {
            throw new NotImplementedException();
        }
    }
}
