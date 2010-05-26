//
// A simple helper class that can be used to stream audio
//
// Author: Miguel de Icaza (miguel@gnome.org)
//
// It hardcodes the behavior and the file type
//
// Use like this:
//    var sp = new StreamingPlayback ();
//    
// You can access FileStream and the OutputQueue properties
//
//    In a loop: sp.FileStream.ParseBytes (...);
//
// When done:
//    sp.FlushAndClose ();
//
using System;
using System.Threading;
using MonoTouch.AudioToolbox;

namespace StreamingAudio
{
	// 
	// Audio decoding: we feed the data that is downloaded from the Http client
	// to the AudioFileStream which decodes the output and generates audio
	// packets.   We feed these decoded audio packets to the OutputAudioQueue
	//		
	// Data is fed by calling StreamingPlayback.FileStream.ParseBytes ()
	//
	public class StreamingPlayback : IDisposable
	{
		AudioFileStream fileStream;		// The decoder
		public OutputAudioQueue OutputQueue;
		AutoResetEvent outputCompleted = new AutoResetEvent (false);
		bool started;
		IntPtr [] outputBuffers;			// The output buffers
		volatile bool [] inuse;
		const int bufferSize = 8 * 1024;
		int bytesFilled;
		int fillBufferIndex;
		int packetsFilled;
		bool startPlay;
		AudioStreamPacketDescription [] pdesc = new AudioStreamPacketDescription [512];
		
		public void Pause ()
		{
			lock (this){
				startPlay = false;
				if (OutputQueue == null)
					return;
				
				OutputQueue.Pause ();
			}
		}
		
		public void Play ()
		{
			lock (this){
				startPlay = true;
				if (OutputQueue == null)
					return;
				
				OutputQueue.Start ();
			}
		}
		
		public StreamingPlayback ()
		{
			fileStream = new AudioFileStream (AudioFileType.MP3);
			fileStream.PacketDecoded += AudioPacketDecoded;
			fileStream.PropertyFound += AudioPropertyFound;
		}
		
		public void ParseBytes (byte [] buffer, int count, bool discontinuity)
		{
			fileStream.ParseBytes (buffer, 0, count, discontinuity);
		}
		
		public void Dispose ()
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}
		
		protected virtual void Dispose (bool disposing)
		{
			// Release unmanaged buffers, flush output, close files.
			if (disposing){
				if (OutputQueue != null)
					OutputQueue.Stop (false);
				
				if (outputBuffers != null)
					foreach (var b in outputBuffers)
						OutputQueue.FreeBuffer (b);
				
				if (fileStream != null){
					fileStream.Close ();
					fileStream = null;
				}
				if (OutputQueue != null){	
					OutputQueue.Dispose ();
					OutputQueue = null;
				}
			}
		}
		
		unsafe void AudioPacketDecoded (object sender, PacketReceivedEventArgs args)
		{
			foreach (var pd in args.PacketDescriptions){
				var packetSize = pd.DataByteSize;
				int left = bufferSize - bytesFilled;
				if (left < packetSize){
					EnqueueBuffer ();
					WaitForBuffer ();
				}
					
				// Fill the buffer
				var buf = outputBuffers [fillBufferIndex];
				AudioQueue.FillAudioData (buf, bytesFilled, args.InputData, (int) pd.StartOffset, packetSize);
				
				// Fill out the packet description
				pdesc [packetsFilled] = pd;
				pdesc [packetsFilled].StartOffset = bytesFilled;
				bytesFilled += packetSize;
				packetsFilled++;
				
				// If we filled out all of our packet descriptions, enqueue the buffer
				if (pdesc.Length == packetsFilled){
					EnqueueBuffer ();
					WaitForBuffer ();
				}
			}
		}
		
		public void FlushAndClose ()
		{
			EnqueueBuffer ();
			OutputQueue.Flush ();
			
			// Release resources
			Dispose ();
		}
		
		void EnqueueBuffer ()
		{
			inuse [fillBufferIndex] = true;
			IntPtr buffer = outputBuffers [fillBufferIndex];
			OutputQueue.EnqueueBuffer (buffer, bytesFilled, pdesc);
			StartQueueIfNeeded ();
		}
		
		void WaitForBuffer ()
		{
			fillBufferIndex++;
			if (fillBufferIndex == outputBuffers.Length)
				fillBufferIndex = 0;
			bytesFilled = 0;
			packetsFilled = 0;
			
			lock (inuse){				
				while (inuse [fillBufferIndex]){
					Monitor.Wait (inuse);
				}
			}
		}
		
		void StartQueueIfNeeded ()
		{
			if (started)
				return;
			OutputQueue.Start ();
			started = true;
		}
		
		// Raised when we are done.
		public event EventHandler Finished;
			
		void AudioPropertyFound (object sender, PropertyFoundEventArgs args)
		{
			switch (args.Property){
				//
				// Enough data has been read that we can start producing output
				//
			case AudioFileStreamProperty.ReadyToProducePackets:
				bytesFilled = 0;
				fillBufferIndex = 0;
				packetsFilled = 0;
				started = false;
				lock (this){
					Console.WriteLine ("Ready");
					OutputQueue = new OutputAudioQueue (fileStream.StreamBasicDescription);
					OutputQueue.OutputCompleted += HandleOutputQueueOutputCompleted;
					
					if (startPlay)
						OutputQueue.Start ();
					
					OutputQueue.Volume = 1;
				}
				
				outputBuffers = new IntPtr [4];
				inuse = new bool [4];
				
				// Allocate audio queue buffers
				for (int i = 0; i < outputBuffers.Length; i++)
					OutputQueue.AllocateBuffer (bufferSize, out outputBuffers [i]);
				
				OutputQueue.MagicCookie = fileStream.MagicCookie;				
				break;
			}
		}
		
		//
		// When the entire audio buffered has been played, the
		// OutputAudioQueue will call us back for more information
		//
		void HandleOutputQueueOutputCompleted (object sender, OutputCompletedEventArgs e)
		{
			IntPtr buf = e.IntPtrBuffer;
			
			for (int i = 0; i < outputBuffers.Length; i++){
				if (buf == outputBuffers [i]){			
					lock (inuse){
						inuse [i] = false;
						Monitor.Pulse (inuse);
					}
					outputCompleted.Set ();
				}
			}
		}
	}
}
