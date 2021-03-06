﻿using Microsoft.SPOT;
using System.Threading;
using GT = Gadgeteer;
using GTI = Gadgeteer.SocketInterfaces;

namespace Gadgeteer.Modules.GHIElectronics {
	/// <summary>A multi-function touch sensor for .Net Gadgeteer.</summary>
	public class TouchL12 : GT.Modules.Module {
		private const ushort I2C_ADDRESS = 0x2B;
		private const int I2C_CLOCK_RATE = 100;
		private const byte IRQ_SRC = 0x0;
		private const byte CAP_STAT_MSB = 0x1;
		private const byte CAP_STAT_LSB = 0x2;
		private const byte WHL_POS_MSB = 0x3;
		private const byte WHL_POS_LSB = 0x4;
		private const byte CAPS = 12;

		private GT.Socket socket;
		private GTI.I2CBus device;
		private GTI.InterruptInput interrupt;
		private GTI.DigitalOutput reset;

		private byte[] readBuffer;
		private byte[] writeBuffer;
		private byte[] addressBuffer;

		private Direction previousSliderDirection;
		private double previousSliderPosition;
		private bool previousSliderTouched;

		private SliderTouchedHandler onSliderTouched;

		private SliderPositionChangedHandler onSliderPositionChanged;

		/// <summary>Delegate representing the slider touch event.</summary>
		/// <param name="sender">The sensor that the event occured on.</param>
		/// <param name="e">The event arguments.</param>
		public delegate void SliderTouchedHandler(TouchL12 sender, SliderTouchedEventArgs e);

		/// <summary>Delegate representing the slider position changed event.</summary>
		/// <param name="sender">The sensor that the event occured on.</param>
		/// <param name="e">The event arguments.</param>
		public delegate void SliderPositionChangedHandler(TouchL12 sender, SliderPositionChangedEventArgs e);

		/// <summary>Fires when the slider is pressed.</summary>
		public event SliderTouchedHandler SliderPressed;

		/// <summary>Fires when the slider is released.</summary>
		public event SliderTouchedHandler SliderReleased;

		/// <summary>Fires when the position of the touch on the slider changes.</summary>
		public event SliderPositionChangedHandler SliderPositionChanged;

		/// <summary>Represents the direction of motion on the slider.</summary>
		public enum Direction {

			/// <summary>Moving towards the left end of the device</summary>
			Left,

			/// <summary>Moving towards the right end of the device</summary>
			Right
		}

		/// <summary>Constructs a new TouchL12 sensor.</summary>
		/// <param name="socketNumber">The socket number the sensor is plugged into.</param>
		public TouchL12(int socketNumber) {
			this.readBuffer = new byte[1];
			this.writeBuffer = new byte[2];
			this.addressBuffer = new byte[1];

			this.socket = GT.Socket.GetSocket(socketNumber, false, this, "I");
			this.reset = GTI.DigitalOutputFactory.Create(this.socket, GT.Socket.Pin.Six, true, this);

			this.Reset();

			this.device = GTI.I2CBusFactory.Create(this.socket, TouchL12.I2C_ADDRESS, TouchL12.I2C_CLOCK_RATE, this);

			this.interrupt = GTI.InterruptInputFactory.Create(socket, GT.Socket.Pin.Three, GTI.GlitchFilterMode.Off, GTI.ResistorMode.PullUp, GTI.InterruptMode.FallingEdge, this);
			this.interrupt.Interrupt += this.OnInterrupt;

			this.previousSliderDirection = (Direction)(-1);
			this.previousSliderPosition = 0;
			this.previousSliderTouched = false;

			Thread.Sleep(250);

			this.ConfigureSPM();
		}

		/// <summary>Gets whether or not the slider is being touched by the user.</summary>
		/// <returns>Whether or not the slider is being touched.</returns>
		public bool IsSliderPressed() {
			return (this.ReadRegister(TouchL12.CAP_STAT_MSB) & 0x10) != 0;
		}

		/// <summary>Gets the current position of the user on the slider.</summary>
		/// <returns>The position of the user's location on the slider between 0 and 11 where 0 is the far left and 11 is the far right.</returns>
		public double GetSliderPosition() {
			ushort whlLsb = this.ReadRegister(TouchL12.WHL_POS_LSB);
			ushort whlMsb = this.ReadRegister(TouchL12.WHL_POS_MSB);
			double position = (double)((whlMsb << 8) + whlLsb);
			return position / 10.0;
		}

		/// <summary>Gets the current direction the user is moving on the slider.</summary>
		/// <returns>The direction the user is moving.</returns>
		public Direction GetSliderDirection() {
			return (this.ReadRegister(TouchL12.CAP_STAT_MSB) & 0x40) != 0 ? Direction.Right : Direction.Left;
		}

		private void OnSliderTouched(TouchL12 sender, SliderTouchedEventArgs e) {
			if (this.onSliderTouched == null)
				this.onSliderTouched = this.OnSliderTouched;

			if (Program.CheckAndInvoke(e.State ? this.SliderPressed : this.SliderReleased, this.onSliderTouched, sender, e)) {
				if (e.State)
					this.SliderPressed(sender, e);
				else
					this.SliderReleased(sender, e);
			}
		}

		private void OnSliderPositionChanged(TouchL12 sender, SliderPositionChangedEventArgs e) {
			if (this.onSliderPositionChanged == null)
				this.onSliderPositionChanged = this.OnSliderPositionChanged;

			if (Program.CheckAndInvoke(this.SliderPositionChanged, this.onSliderPositionChanged, sender, e))
				this.SliderPositionChanged(sender, e);
		}

		private void Reset() {
			Thread.Sleep(100);
			this.reset.Write(false);
			Thread.Sleep(100);
			this.reset.Write(true);
			Thread.Sleep(100);
		}

		private void OnInterrupt(GTI.InterruptInput sender, bool value) {
			byte flags = this.ReadRegister(TouchL12.IRQ_SRC);

			if ((flags & 0x8) != 0) {
				double position = this.GetSliderPosition();
				bool touched = this.IsSliderPressed();
				Direction direction = this.GetSliderDirection();

				if (touched != this.previousSliderTouched)
					this.OnSliderTouched(this, new SliderTouchedEventArgs(touched));

				if (position != this.previousSliderPosition || direction != this.previousSliderDirection)
					this.OnSliderPositionChanged(this, new SliderPositionChangedEventArgs(direction, position));

				this.previousSliderTouched = touched;
				this.previousSliderPosition = position;
				this.previousSliderDirection = direction;
			}
		}

		private void ConfigureSPM() {
			//SPM must be written in 8 byte segments, so that is why we have the extra stuff below.

			//0x1 is cap sensitivity mode, 0xFF's are the cap type modes, 0x0 is cap 0 and 1 sensitivty, the last 3 0's set cap 2-7 sensitivity.
			this.WriteSPM(0x9, new byte[] { 0x0, 0x1, 0xFF, 0xFF, 0xFF, 0x0, 0x0, 0x0, 0x0 });
		}

		private void WriteSPM(byte address, byte[] values) {
			this.WriteRegister(0x0D, 0x10);
			this.WriteRegister(0x0E, address);

			this.device.Write(values); //needs to begin with a 0 representing the I2C address of 0.

			this.WriteRegister(0x0D, 0x00);
		}

		private void WriteRegister(byte address, byte value) {
			this.writeBuffer[0] = address;
			this.writeBuffer[1] = value;
			this.device.Write(this.writeBuffer);
		}

		private byte ReadRegister(byte address) {
			this.addressBuffer[0] = address;
			this.device.WriteRead(this.addressBuffer, this.readBuffer);
			return this.readBuffer[0];
		}
		/// <summary>Event arguments for the SliderPositionChanged event.</summary>
		public class SliderPositionChangedEventArgs : EventArgs {

			/// <summary>The position of the touch on the slider between 0 and 11.</summary>
			public double Position { get; private set; }

			/// <summary>The direction of movement on the slider.</summary>
			public Direction Direction { get; private set; }

			internal SliderPositionChangedEventArgs(Direction direction, double position) {
				this.Direction = direction;
				this.Position = position;
			}
		}

		/// <summary>Event arguments for the SliderTouched event.</summary>
		public class SliderTouchedEventArgs : EventArgs {

			/// <summary>Whether or not the slider was touched.</summary>
			public bool State { get; private set; }

			internal SliderTouchedEventArgs(bool state) {
				this.State = state;
			}
		}
	}
}