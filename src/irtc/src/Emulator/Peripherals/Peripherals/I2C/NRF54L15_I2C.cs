using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.I2C
{
    /// <summary>
    /// Class <c>NRF52840_I2C</c> Models the two-wire interface on the NRF54L15
    /// </summary>
    public class NRF54L15_I2C : SimpleContainer<II2CPeripheral>, IProvidesRegisterCollection<DoubleWordRegisterCollection>, IDoubleWordPeripheral, IKnownSize
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NRF54L15_I2C"/> class.
        /// </summary>
        /// <param name="machine">TODO: Understand this class</param>
        public NRF54L15_I2C(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();

            rxBuffer = new Queue<byte>();
            txBuffer = new Queue<byte>();

            RegistersCollection = new DoubleWordRegisterCollection(this);
            DefineRegisters();
        }

        /// <summary>
        /// Resets the RX and TX buffers, private flags, registers, and interrupts
        /// </summary>
        public override void Reset()
        {
            rxBuffer.Clear();
            txBuffer.Clear();

            selectedPeripheral = null;
            enabled = false;
            transmissionInProgress = false;

            RegistersCollection.Reset();
            UpdateInterrupts();
        }

        /// <summary>
        /// Read from a register
        /// </summary>
        /// <param name="offset">Register offset</param>
        /// <returns>Data in register</returns>
        public uint ReadDoubleWord(long offset)
        {
            return RegistersCollection.Read(offset);
        }

        /// <summary>
        /// Write to a register
        /// </summary>
        /// <param name="offset">Register offset</param>
        /// <param name="value">Data to write to the register</param>
        public void WriteDoubleWord(long offset, uint value)
        {
            RegistersCollection.Write(offset, value);
        }

        public GPIO IRQ { get; }

        public long Size => 0x1000;

        public DoubleWordRegisterCollection RegistersCollection { get; }

        /// <summary>
        /// Initialize the registers for the <see cref="NRF54L15_I2C"/> peripheral
        /// </summary>
        private void DefineRegisters()
        {
            Registers.StopTransaction.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_STOP", writeCallback: (_, val) =>
                {
                    if(!val)
                    {
                        return;
                    }

                    StopTransmission();
                })
                .WithReservedBits(1, 31)
            ;

            Registers.ResumeTransaction.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_RESUME", writeCallback: (_, val) =>
                {
                    if(!val)
                    {
                        return;
                    }

                    if(!transmissionInProgress)
                    {
                        return;
                    }

                    TryFillReceivedBuffer(true);
                })
                .WithReservedBits(1, 31)
            ;

            Registers.StartReceivingDma.Define(this)
                .WithFlag(0, FieldMode.Write, name: "TASKS_DMA.RX.START", writeCallback: (_, val) =>
                {
                    if(!val)
                    {
                        return;
                    }

                    transmissionInProgress = true;
                    // send what is buffered as this might be a repeated start condition
                    TrySendDataToPeripheral();
                    // prepare to receive data from peripheral
                    rxBuffer.Clear();
                    // wait for writing bytes to TransferBuffer...
                })
                .WithReservedBits(1, 31)
            ;

            Registers.StoppedInterruptPending.Define(this)
                .WithFlag(0, out stoppedInterruptPending, name: "EVENTS_STOPPED")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.RxInterruptPending.Define(this)
                .WithFlag(0, out rxInterruptPending, name: "EVENTS_RXREADY")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.TxInterruptPending.Define(this)
                .WithFlag(0, out txInterruptPending, name: "EVENTS_TXDSENT")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.ErrorInterruptPending.Define(this)
                .WithFlag(0, out errorInterruptPending, name: "EVENTS_ERROR")
                .WithReservedBits(1, 31)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.ErrorSource.Define(this)
                .WithTaggedFlag("OVERRUN", 0)
                .WithFlag(1, out addressNackError, name: "ANACK")
                .WithTaggedFlag("DNACK", 2)
                .WithReservedBits(3, 29)
            ;

            Registers.Shortcuts.Define(this)
                .WithTag("BB_SUSPEND", 0, 1)
                .WithFlag(1, out byteBoundaryStopShortcut, name: "BB_STOP")
                .WithReservedBits(2, 30)
            ;

            Registers.SetEnableInterrupts.Define(this)
                .WithReservedBits(0, 1)
                .WithFlag(1, out stoppedInterruptEnabled, FieldMode.Read | FieldMode.Set, name: "STOPPED")
                .WithFlag(2, out rxInterruptEnabled, FieldMode.Read | FieldMode.Set, name: "RXREADY")
                .WithReservedBits(3, 4)
                .WithFlag(7, out txInterruptEnabled, FieldMode.Read | FieldMode.Set, name: "TXDSENT")
                .WithReservedBits(8, 1)
                .WithFlag(9, out errorInterruptEnabled, FieldMode.Read | FieldMode.Set, name: "ERROR")
                .WithReservedBits(10, 4)
                .WithFlag(14, name: "BB") // this is a flag to limit warnings, we don't support the byte-boundary interrupt
                .WithReservedBits(15, 3)
                .WithFlag(18, name: "SUSPENDED") // this is a flag to limit warnings, we don't support the suspended interrupt
                .WithReservedBits(19, 13)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.ClearEnableInterrupts.Define(this)
                .WithReservedBits(0, 1)
                .WithFlag(1, name: "STOPPED",
                    writeCallback: (_, val) => { if(val) stoppedInterruptEnabled.Value = false; },
                    valueProviderCallback: _ => stoppedInterruptEnabled.Value)
                .WithFlag(2, name: "RXREADY",
                    writeCallback: (_, val) => { if(val) rxInterruptEnabled.Value = false; },
                    valueProviderCallback: _ => rxInterruptEnabled.Value)
                .WithReservedBits(3, 4)
                .WithFlag(7, name: "TXDSENT",
                    writeCallback: (_, val) => { if(val) txInterruptEnabled.Value = false; },
                    valueProviderCallback: _ => txInterruptEnabled.Value)
                .WithReservedBits(8, 1)
                .WithFlag(9, name: "ERROR",
                    writeCallback: (_, val) => { if(val) errorInterruptEnabled.Value = false; },
                    valueProviderCallback: _ => errorInterruptEnabled.Value)
                .WithReservedBits(10, 4)
                .WithFlag(14, name: "BB") // this is a flag to limit warnings, we don't support the byte-boundary interrupt
                .WithReservedBits(15, 3)
                .WithFlag(18, name: "SUSPENDED") // this is a flag to limit warnings, we don't support the suspended interrupt
                .WithReservedBits(19, 13)
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            Registers.Enable.Define(this)
                .WithValueField(0, 4, writeCallback: (_, val) =>
                {
                    switch(val)
                    {
                        case 0:
                            enabled = false;
                            break;

                        case 5:
                            enabled = true;
                            break;

                        default:
                            this.Log(LogLevel.Warning, "Wrong enabled value");
                            break;
                    }
                })
                .WithReservedBits(4, 28)
            ;

            Registers.ReceiveBuffer.Define(this)
                .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ =>
                {
                    if(!TryReadFromPeripheral(out var result))
                    {
                        this.Log(LogLevel.Warning, "Trying to read from an empty fifo");
                        result = 0;
                    }

                    if(byteBoundaryStopShortcut.Value)
                    {
                        StopTransmission();
                    }

                    return result;
                })
                .WithReservedBits(8, 24)
            ;

            Registers.TransferBuffer.Define(this)
                .WithValueField(0, 8, writeCallback: (_, val) =>
                {
                    if(selectedPeripheral == null)
                    {
                        this.Log(LogLevel.Warning, "No peripheral is currently attached at selected address 0x{0:X}", address.Value);
                        addressNackError.Value = true;
                        errorInterruptPending.Value = true;
                        UpdateInterrupts();
                        return;
                    }

                    this.Log(LogLevel.Noisy, "Enqueuing byte 0x{0:X}", val);
                    txBuffer.Enqueue((byte)val);

                    txInterruptPending.Value = true;
                    UpdateInterrupts();
                })
                .WithReservedBits(8, 24)
            ;

            Registers.Address.Define(this)
                .WithValueField(0, 7, out address, writeCallback: (_, val) =>
                {
                    if(!TryGetByAddress((int)val, out selectedPeripheral))
                    {
                        this.Log(LogLevel.Warning, "Tried to select a not-connected peripheral at address 0x{0:X}", val);
                    }
                })
                .WithReservedBits(8, 24)
            ;
        }

        private bool TryFillReceivedBuffer(bool generateInterrupt)
        {
            if(selectedPeripheral == null)
            {
                return false;
            }

            if(!rxBuffer.Any())
            {
                var data = selectedPeripheral.Read();
                rxBuffer.EnqueueRange(data);
            }

            if(rxBuffer.Any())
            {
                if(generateInterrupt)
                {
                    rxInterruptPending.Value = true;
                    UpdateInterrupts();
                }
                return true;
            }

            return false;
        }

        private bool TryReadFromPeripheral(out byte b)
        {
            if(!enabled)
            {
                this.Log(LogLevel.Warning, "Tried to read data on a disabled controller");
                b = 0;
                return false;
            }

            if(!rxBuffer.TryDequeue(out b))
            {
                TryFillReceivedBuffer(false);
                if(!rxBuffer.TryDequeue(out b))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Writes all of the data in the TX buffer to the selected peripheral
        /// </summary>
        /// <returns>True if data was sent</returns>
        private bool TrySendDataToPeripheral()
        {
            if(!enabled)
            {
                this.Log(LogLevel.Warning, "Tried to send data on a disabled controller");
                return false;
            }

            if(!txBuffer.Any())
            {
                return false;
            }

            if(selectedPeripheral == null)
            {
                this.Log(LogLevel.Warning, "No peripheral is currently attached at selected address 0x{0:X}", address.Value);
                return false;
            }

            var data = txBuffer.DequeueAll();
            this.Log(LogLevel.Noisy, "Sending {0} bytes to the device {1}", data.Length, address.Value);
            selectedPeripheral.Write(data);

            return true;
        }

        private void StopTransmission()
        {
            transmissionInProgress = false;

            // send out buffered data to peripheral;
            // in reality there is no fifo - each
            // byte is sent right away, but our
            // I2C interface in Renode works a bit
            // different
            TrySendDataToPeripheral();

            selectedPeripheral?.FinishTransmission();

            stoppedInterruptPending.Value = true;
            UpdateInterrupts();
        }

        /// <summary>
        /// Sets the <see cref="NRF54L15_I2C"/> interrupt if any of the private interrupts are pending
        /// </summary>
        private void UpdateInterrupts()
        {
            var flag = false;

            flag |= txInterruptEnabled.Value && txInterruptPending.Value;
            flag |= rxInterruptEnabled.Value && rxInterruptPending.Value;
            flag |= stoppedInterruptEnabled.Value && stoppedInterruptPending.Value;
            flag |= errorInterruptEnabled.Value && errorInterruptPending.Value;

            this.Log(LogLevel.Noisy, "Setting IRQ to {0}", flag);
            IRQ.Set(flag);
        }

        private readonly Queue<byte> rxBuffer;
        private readonly Queue<byte> txBuffer;

        private II2CPeripheral selectedPeripheral;
        private bool enabled;
        private bool transmissionInProgress;

        private IValueRegisterField address;
        private IFlagRegisterField txInterruptPending;
        private IFlagRegisterField txInterruptEnabled;

        private IFlagRegisterField rxInterruptPending;
        private IFlagRegisterField rxInterruptEnabled;

        private IFlagRegisterField errorInterruptPending;
        private IFlagRegisterField errorInterruptEnabled;

        private IFlagRegisterField stoppedInterruptPending;
        private IFlagRegisterField stoppedInterruptEnabled;

        private IFlagRegisterField byteBoundaryStopShortcut;

        private IFlagRegisterField addressNackError;

        private enum Registers
        {
            StopTransaction = 0x004,
            SuspendTransaction = 0x00C,
            ResumeTransaction = 0x010,
            StartReceivingDma = 0x028,
            StopReceivingDma = 0x02C,
            EnableReceivingDmaMatchEvent = 0x030,
            DisableReceivingDmaMatchEvent = 0x040,
            StartTransmittingDma = 0x050,
            StopTransmittingDma = 0x054,
            SubscribeToStopTask = 0x084,
            SubscribeToSuspendTask = 0x08C,
            SubscribeToResumeTask = 0x090,
            SubscribeToReceivingDmaStartTask = 0x0A8,
            SubscribeToReceivingDmaStopTask = 0x0AC,
            SubscribeToReceivingDmaEnableMatchTask = 0x0B0,
            SubscribeToReceivingDmaDisableMatchTask = 0x0C0,
            SubscribeToTransmittingDmaStartTask = 0x0D0,
            SubscribeToTransmittingDmaStopTask = 0x0D4,
            StoppedInterruptPending = 0x104,
            ErrorInterruptPending = 0x114,
            SuspendedInterruptPending = 0x128,
            LastReceiveInterruptPending = 0x134,
            LastTransmitInterruptPending = 0x138,
            ReceivingDmaEndInterruptPending = 0x14C,
            ReceivingDmaReadyInterruptPending = 0x150,
            ReceivingDmaBusErrorInterruptPending = 0x154,
            PublishStoppedEvent = 0x184,
            PublishErrorEvent = 0x194,
            PublishSuspendEvent = 0x1A8,
            PublishLastReceiveEvent = 0x1B4,
            PublishLastTransmitEvent = 0x1B8,
            PublishReceivingDmaEndEvent = 0x1CC,
            PublishReceivingDmaReadyEvent = 0x1D0,
            PublishReceivingDmaBusErrorEvent = 0x1D4,
            PublishReceivingDmaMatchEvent = 0x1D8,
            PublishTransmittingDmaEndEvent = 0x1E8,
            PublishTransmittingDmaReadyEvent = 0x1EC,
            PublishTransmittingDmaBusErrorEvent = 0x1F0,
            Shortcuts = 0x200,
            InterruptEnable = 0x300,
            SetInterruptEnable = 0x304,
            ClearInterruptEnable = 0x308,
            ErrorSource = 0x4C4,
            Enable = 0x500,
            Frequency = 0x524,
            Address = 0x588,
            PinSelectSCL = 0x600,
            PinSelectSDA = 0x604,
            ReceiveBuffer = 0x704,
            DmaReceivingBufferMaxCount = 0x708,
            DmaReceivingBufferAmount = 0x70C,
            DmaReceivingTerminateOnBusError = 0x71C,
            DmaReceivingBusErrorAddress = 0x720,
            DmaReceivingMatchConfig = 0x724,
            DmaReceivingMatchCandidate = 0x728,
            TransmitBuffer = 0x73C,
            DmaTransmittingBufferMaxCount = 0x740,
            DmaTransmittingBufferAmount = 0x744,
            DmaTransmittingTerminateOnBusError = 0x754,
            DmaTransmittingBusErrorAddress = 0x758,
        }
    }
}

