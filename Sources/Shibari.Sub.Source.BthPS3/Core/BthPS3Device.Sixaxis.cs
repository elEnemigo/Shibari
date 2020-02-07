﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PInvoke;
using Serilog;
using Shibari.Sub.Core.Shared.Types.Common;
using Shibari.Sub.Core.Shared.Types.DualShock3;
using Shibari.Sub.Core.Util;

namespace Shibari.Sub.Source.BthPS3.Core
{
    internal abstract partial class BthPS3Device
    {
        private class SixaxisDevice : BthPS3Device
        {
            private readonly byte[] _hidEnableCommand =
            {
                0x53, // HID BT Set_report (0x50) | Report Type (Feature 0x03)
                0xF4, // Report ID
                0x42, // Special PS3 Controller enable commands
                0x03,
                0x00,
                0x00
            };

            private readonly byte[][] _hidInitReports =
            {
                new byte[] {0x02, 0x00, 0x0F, 0x00, 0x08, 0x35, 0x03, 0x19, 0x12, 0x00, 0x00, 0x03, 0x00},
                new byte[]
                {
                    0x04, 0x00, 0x10, 0x00, 0x0F, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10, 0x35, 0x06, 0x09, 0x02, 0x01, 0x09,
                    0x02,
                    0x02, 0x00
                },
                new byte[]
                    {0x06, 0x00, 0x11, 0x00, 0x0D, 0x35, 0x03, 0x19, 0x11, 0x24, 0x01, 0x90, 0x35, 0x03, 0x09, 0x02, 0x06, 0x00},
                new byte[]
                {
                    0x06, 0x00, 0x12, 0x00, 0x0F, 0x35, 0x03, 0x19, 0x11, 0x24, 0x01, 0x90, 0x35, 0x03, 0x09, 0x02, 0x06,
                    0x02,
                    0x00, 0x7F
                },
                new byte[]
                {
                    0x06, 0x00, 0x13, 0x00, 0x0F, 0x35, 0x03, 0x19, 0x11, 0x24, 0x01, 0x90, 0x35, 0x03, 0x09, 0x02, 0x06,
                    0x02,
                    0x00, 0x59
                },
                new byte[]
                {
                    0x06, 0x00, 0x14, 0x00, 0x0F, 0x35, 0x03, 0x19, 0x11, 0x24, 0x01, 0x80, 0x35, 0x03, 0x09, 0x02, 0x06,
                    0x02,
                    0x00, 0x33
                },
                new byte[]
                {
                    0x06, 0x00, 0x15, 0x00, 0x0F, 0x35, 0x03, 0x19, 0x11, 0x24, 0x01, 0x90, 0x35, 0x03, 0x09, 0x02, 0x06,
                    0x02,
                    0x00, 0x0D
                }
            };

            //
            // Values indicating which of the four LEDs to toggle
            // 
            private readonly byte[] _ledOffsets = { 0x02, 0x04, 0x08, 0x10 };

            public SixaxisDevice(string path, Kernel32.SafeObjectHandle handle, int index) : base(path, handle, index)
            {
                DeviceType = DualShockDeviceType.DualShock3;
                //
                // Remote MAC address is encoded in path as InstanceId
                // This is a lazy approach but saves an I/O request ;)
                // 
                ClientAddress = PhysicalAddress.Parse(path.Substring(path.LastIndexOf('&') + 1, 12));

                // 
                // Initialize default output report native buffer
                // 
                Marshal.Copy(HidOutputReport, 0, OutputReportBuffer, HidOutputReport.Length);

                //
                // Crude way to assign device index as LED number
                // 
                if (index >= 0 && index < 4)
                    Marshal.WriteByte(OutputReportBuffer, 11, _ledOffsets[index]);

                //
                // Send initialization packets
                //
                Task.Factory.StartNew(Init);
            }

            private async Task Init()
            {
                await Task.Delay(TimeSpan.FromSeconds(1));

                foreach (var report in _hidInitReports)
                {
                    SendHidCommand(report);
                    await Task.Delay(TimeSpan.FromMilliseconds(10));
                }

                await Task.Delay(TimeSpan.FromSeconds(1));

                //
                // Send the start command to remote device
                // 
                SendHidCommand(_hidEnableCommand);

                await Task.Delay(TimeSpan.FromSeconds(1));

                //
                // Send initial output report
                // 
                SendHidCommand(OutputReportBuffer, OutputReportBufferSize);
            }

            protected override byte[] HidOutputReport => new byte[]
            {
                0x52, /* HID BT Set_report (0x50) | Report Type (Output 0x02)*/
                0x01, /* Report ID */
                0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00,
                0xff, 0x27, 0x10, 0x00, 0x32,
                0xff, 0x27, 0x10, 0x00, 0x32,
                0xff, 0x27, 0x10, 0x00, 0x32,
                0xff, 0x27, 0x10, 0x00, 0x32,
                0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };

            public override void PairTo(PhysicalAddress host)
            {
                throw new NotSupportedException("You can not change the host address while connected via Bluetooth.");
            }

            public override void Rumble(byte largeMotor, byte smallMotor)
            {
                SetRumbleOn(largeMotor, (byte)(smallMotor > 0 ? 0x01 : 0x00));
            }

            [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
            private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

            protected void SetRumbleOn(RumbleEnum mode)
            {
                var power = new byte[] { 0xff, 0x00 }; // Defaults to RumbleLow
                if (mode == RumbleEnum.RumbleHigh)
                {
                    power[0] = 0x00;
                    power[1] = 0xff;
                }

                SetRumbleOn(power[1], power[0]);
            }

            protected void SetRumbleOn(byte largePower, byte smallPower, byte largeDuration = 0xfe,
                byte smallDuration = 0xfe)
            {
                var rumbleBuffer = Marshal.AllocHGlobal(OutputReportBufferSize);

                try
                {
                    CopyMemory(rumbleBuffer, OutputReportBuffer, OutputReportBufferSize);

                    Marshal.WriteByte(rumbleBuffer, 3, smallDuration);
                    Marshal.WriteByte(rumbleBuffer, 4, smallPower);
                    Marshal.WriteByte(rumbleBuffer, 5, largeDuration);
                    Marshal.WriteByte(rumbleBuffer, 6, largePower);

                    SendHidCommand(rumbleBuffer, OutputReportBufferSize);
                }
                finally
                {
                    Marshal.FreeHGlobal(rumbleBuffer);
                }
            }

            protected void SetRumbleOff()
            {
                Marshal.WriteByte(OutputReportBuffer, 3, 0x00); // Rumble
                Marshal.WriteByte(OutputReportBuffer, 4, 0x00); // Rumble
                Marshal.WriteByte(OutputReportBuffer, 5, 0x00); // Rumble
                Marshal.WriteByte(OutputReportBuffer, 6, 0x00); // Rumble

                SendHidCommand(OutputReportBuffer, OutputReportBufferSize);
            }

            protected void SetAllOff()
            {
                Marshal.WriteByte(OutputReportBuffer, 3, 0x00); // Rumble
                Marshal.WriteByte(OutputReportBuffer, 4, 0x00); // Rumble
                Marshal.WriteByte(OutputReportBuffer, 5, 0x00); // Rumble
                Marshal.WriteByte(OutputReportBuffer, 6, 0x00); // Rumble

                Marshal.WriteByte(OutputReportBuffer, 11, 0x00); // LED

                SendHidCommand(OutputReportBuffer, OutputReportBufferSize);
            }

            protected void SendHidCommand(byte[] buffer)
            {
                var unmanagedBuffer = Marshal.AllocHGlobal(buffer.Length);
                Marshal.Copy(buffer, 0, unmanagedBuffer, buffer.Length);

                try
                {
                    var ret = DeviceHandle.OverlappedDeviceIoControl(
                        IOCTL_BTHPS3_HID_CONTROL_WRITE,
                        unmanagedBuffer,
                        buffer.Length,
                        IntPtr.Zero,
                        0,
                        out _
                    );

                    if (!ret)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally
                {
                    Marshal.FreeHGlobal(unmanagedBuffer);
                }
            }

            protected void SendHidCommand(IntPtr buffer, int bufferLength)
            {
                var ret = DeviceHandle.OverlappedDeviceIoControl(
                    IOCTL_BTHPS3_HID_CONTROL_WRITE,
                    buffer,
                    bufferLength,
                    IntPtr.Zero,
                    0,
                    out _
                );

                if (!ret)
                    OnDisconnected();
            }

            protected override void OnOutputReport(long l)
            {

            }

            private static int? GetReportSize(IEnumerable<byte> buffer, out int remaining)
            {
                return GetReportSize(buffer.ToArray(), out remaining);
            }

            private static int? GetReportSize(byte[] buffer, out int remaining)
            {
                // search for packet start sequence
                var startPattern = new byte[] { 0xA1, 0x01 };
                remaining = 0;

                // occurrence of 1st sequence
                var firstPacketStart = buffer.IndexOf(startPattern);

                // not found, error
                if (firstPacketStart == -1)
                    return null;

                // occurrence of 2nd sequence
                var secondPacketStart = buffer.Skip(startPattern.Length).IndexOf(startPattern) + startPattern.Length;
                // packet length
                var length = secondPacketStart - firstPacketStart;
                // how many bytes left to consume to align next packet start
                remaining = buffer.Length - (secondPacketStart + length);

                return (length > 0) ? length : (int?)null;
            }

            protected override void RequestInputReportWorker(object cancellationToken)
            {
                var token = (CancellationToken)cancellationToken;
                var buffer = new byte[/* 0x64 */ 256];
                var unmanagedBuffer = Marshal.AllocHGlobal(buffer.Length);
                int? size = null;

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var ret = DeviceHandle.OverlappedDeviceIoControl(
                            IOCTL_BTHPS3_HID_INTERRUPT_READ,
                            IntPtr.Zero,
                            0,
                            unmanagedBuffer,
                            buffer.Length,
                            out _
                        );

                        if (!ret)
                        {
                            OnDisconnected();
                            return;
                        }

                        Marshal.Copy(unmanagedBuffer, buffer, 0, buffer.Length);

                        try
                        {
                            // initially correct packet size is unknown
                            if (!size.HasValue)
                            {
                                int remaining, offset = 0;

                                do
                                {
                                    // get buffer size and alignment
                                    size = GetReportSize(buffer.Skip(offset), out remaining);

                                    if (!size.HasValue)
                                        throw new InvalidDataException("Computing packet size failed");

                                    // shift one packet forward
                                    offset += size.Value;

                                    // until remaining buffer resembles incomplete packet
                                } while (remaining > size.Value);

                                remaining = size.Value - remaining;

                                // consume 
                                ret = DeviceHandle.OverlappedDeviceIoControl(
                                    IOCTL_BTHPS3_HID_INTERRUPT_READ,
                                    IntPtr.Zero,
                                    0,
                                    unmanagedBuffer,
                                    remaining,
                                    out var consumed
                                );

                                // catch error
                                if (!ret || remaining != consumed)
                                {
                                    Log.Fatal("Consuming remaining bytes failed");
                                    OnDisconnected();
                                    return;
                                }

                                // re-allocate buffers with correct size
                                Marshal.FreeHGlobal(unmanagedBuffer);
                                buffer = new byte[size.Value];
                                unmanagedBuffer = Marshal.AllocHGlobal(buffer.Length);
                                continue;
                            }

                            if (DumpInputReport)
                            {
                                Log.Information("Input Report: {Report}", buffer.ToHexString());
                            }

                            if (!SuppressInputReport)
                            {
                                OnInputReport(DualShock3InputReport.FromBuffer(buffer.Skip(1).ToArray()));
                            }
                        }
                        catch (InvalidDataException ide)
                        {
                            Log.Warning("Malformed input report received: {Exception}", ide);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("{Exception}", ex);
                }
                finally
                {
                    Marshal.FreeHGlobal(unmanagedBuffer);
                }
            }

            protected enum RumbleEnum
            {
                RumbleHigh = 0x10,
                RumbleLow = 0x20
            }
        }
    }
}
