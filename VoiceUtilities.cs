namespace OpenVoiceSharp
{
    public static class VoiceUtilities
    {
        /// <summary>
        /// Gets the sample size for a frame.
        /// </summary>
        /// <param name="channels">Set 1 for mono and 2 for stereo</param>
        /// <param name="float32">Float32 size is half</param>
        /// <returns></returns>
        public static int GetSampleSize(int sampleRate, int timeLengthMs, int channels)
            => ((int)(sampleRate * 16f / 8f * (timeLengthMs / 1000f) * channels));

        /// <summary>
        /// Gets the sample size for a frame.
        /// </summary>
        /// <param name="channels">Set 1 for mono and 2 for stereo</param>
        /// <param name="float32">Float32 size is half</param>
        /// <returns></returns>
        public static int GetSampleSize(int channels)
            => GetSampleSize(VoiceChatInterface.SampleRate, VoiceChatInterface.FrameLength, channels);

        /// <summary>
        /// Converts 16 bit PCM data into float 32.
        /// Note that the float array must be half the size of the byte array.
        /// </summary>
        /// <param name="input">The 16 bit PCM data according to your needs.</param>
        /// <param name="output">The output data in which the result will be returned.</param>
        /// <returns>The 16 bit byte array.</returns>
        public static void Convert16BitToFloat(byte[] input, float[] output)
            => Convert16BitToFloat(input, output, input.Length);

        /// <summary>
        /// Converts 16 bit PCM data into float 32 for a specific input length.
        /// </summary>
        /// <param name="input">The 16 bit PCM data according to your needs.</param>
        /// <param name="output">The output data in which the result will be returned.</param>
        /// <param name="inputLengthBytes">How many bytes to convert from <paramref name="input"/>.</param>
        public static void Convert16BitToFloat(byte[] input, float[] output, int inputLengthBytes)
        {
            if (input is null)
                throw new ArgumentNullException(nameof(input));
            if (output is null)
                throw new ArgumentNullException(nameof(output));
            if (inputLengthBytes < 0 || inputLengthBytes > input.Length)
                throw new ArgumentOutOfRangeException(nameof(inputLengthBytes));
            if ((inputLengthBytes & 1) != 0)
                throw new ArgumentException("Input length must be even for 16-bit PCM.", nameof(inputLengthBytes));

            int samples = inputLengthBytes / 2;
            if (output.Length < samples)
                throw new ArgumentException("Output buffer is too small for the requested input length.", nameof(output));

            for (int n = 0; n < samples; n++)
            {
                short sample = BitConverter.ToInt16(input, n * 2);
                output[n] = sample / 32768f;
            }
        }

        /// <summary>
        /// Converts float 32 PCM data into 16 bit.
        /// Note that the byte array must be double the size of the float array.
        /// </summary>
        /// <param name="input">The float 32 PCM data according to your needs.</param>
        /// <param name="output">The output data in which the result will be returned.</param>
        /// <returns>The float32 PCM array.</returns>
        public static void ConvertFloatTo16Bit(float[] input, byte[] output)
            => ConvertFloatTo16Bit(input, output, output.Length);

        /// <summary>
        /// Converts float 32 PCM data into 16 bit for a specific output length.
        /// </summary>
        /// <param name="input">The float 32 PCM data according to your needs.</param>
        /// <param name="output">The output data in which the result will be returned.</param>
        /// <param name="outputLengthBytes">How many bytes to write into <paramref name="output"/>.</param>
        public static void ConvertFloatTo16Bit(float[] input, byte[] output, int outputLengthBytes)
        {
            if (input is null)
                throw new ArgumentNullException(nameof(input));
            if (output is null)
                throw new ArgumentNullException(nameof(output));
            if (outputLengthBytes < 0 || outputLengthBytes > output.Length)
                throw new ArgumentOutOfRangeException(nameof(outputLengthBytes));
            if ((outputLengthBytes & 1) != 0)
                throw new ArgumentException("Output length must be even for 16-bit PCM.", nameof(outputLengthBytes));

            int samples = outputLengthBytes / 2;
            if (input.Length < samples)
                throw new ArgumentException("Input buffer is too small for the requested output length.", nameof(input));

            int sampleIndex = 0, pcmIndex = 0;
            while (sampleIndex < samples)
            {
                float sample = input[sampleIndex];
                if (sample > 1f) sample = 1f;
                else if (sample < -1f) sample = -1f;

                short outsample = (short)(sample * short.MaxValue);
                output[pcmIndex] = (byte)(outsample & 0xff);
                output[pcmIndex + 1] = (byte)((outsample >> 8) & 0xff);

                sampleIndex++;
                pcmIndex += 2;
            }
        }
    }
}
