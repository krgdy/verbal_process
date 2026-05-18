using System;
using System.IO;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// 오디오 클리핑 및 포맷 변환 유틸리티 클래스
    /// </summary>
    public static class AudioUtils
    {
        private const int TargetSampleRate = 16000;

        /// <summary>
        /// 원본 AudioClip에서 특정 구간을 잘라내어 새로운 AudioClip으로 반환합니다.
        /// </summary>
        public static AudioClip TrimAudio(AudioClip source, int startSample, int endSample)
        {
            int sourceSamples = source.samples;
            int length;

            if (endSample >= startSample)
            {
                length = endSample - startSample;
            }
            else
            {
                length = (sourceSamples - startSample) + endSample;
            }

            if (length <= 0) return null;

            float[] data = new float[length];
            
            if (endSample >= startSample)
            {
                source.GetData(data, startSample);
            }
            else
            {
                int firstPartLength = sourceSamples - startSample;
                float[] firstPart = new float[firstPartLength];
                source.GetData(firstPart, startSample);
                Array.Copy(firstPart, 0, data, 0, firstPartLength);

                float[] secondPart = new float[endSample];
                source.GetData(secondPart, 0);
                Array.Copy(secondPart, 0, data, firstPartLength, endSample);
            }

            AudioClip result = AudioClip.Create("TrimmedAudio", length, source.channels, source.frequency, false);
            result.SetData(data, 0);

            return result;
        }

        /// <summary>
        /// AudioClip을 16kHz Mono WAV 형식의 byte array로 변환합니다. (헤더 포함)
        /// </summary>
        public static byte[] GetWavBytes(AudioClip clip)
        {
            byte[] pcmData = GetRawPcmBytes(clip);

            using (var memoryStream = new MemoryStream())
            {
                WriteWavHeader(memoryStream, pcmData.Length / 2, TargetSampleRate, 1);
                memoryStream.Write(pcmData, 0, pcmData.Length);
                return memoryStream.ToArray();
            }
        }

        /// <summary>
        /// AudioClip을 16kHz Mono 순수 PCM(int16) byte array로 변환합니다. (헤더 미포함)
        /// </summary>
        public static byte[] GetRawPcmBytes(AudioClip clip)
        {
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);

            // 1. 스테레오인 경우 모노로 믹싱
            float[] monoSamples = MixToMono(samples, clip.channels);

            // 2. 16kHz로 리샘플링
            float[] resampledSamples = Resample(monoSamples, clip.frequency, TargetSampleRate);

            // 3. 바이트 변환
            return ConvertToPcmBytes(resampledSamples);
        }

        private static float[] MixToMono(float[] input, int channels)
        {
            if (channels == 1) return input;
            float[] output = new float[input.Length / channels];
            for (int i = 0; i < output.Length; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    sum += input[i * channels + c];
                }
                output[i] = sum / channels;
            }
            return output;
        }

        private static float[] Resample(float[] samples, int fromRate, int toRate)
        {
            if (fromRate == toRate) return samples;
            
            float ratio = (float)fromRate / toRate;
            int newLength = Mathf.FloorToInt(samples.Length / ratio);
            float[] result = new float[newLength];

            for (int i = 0; i < newLength; i++)
            {
                float index = i * ratio;
                int i1 = Mathf.FloorToInt(index);
                int i2 = Mathf.Min(i1 + 1, samples.Length - 1);
                float t = index - i1;
                result[i] = Mathf.Lerp(samples[i1], samples[i2], t);
            }
            return result;
        }

        private static byte[] ConvertToPcmBytes(float[] samples)
        {
            byte[] bytesData = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                // short 변환 및 비트 연산으로 최적화
                short value = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32767f);
                bytesData[i * 2] = (byte)(value & 0xff);
                bytesData[i * 2 + 1] = (byte)((value >> 8) & 0xff);
            }
            return bytesData;
        }

        private static void WriteWavHeader(MemoryStream stream, int samplesCount, int hz, int channels)
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes("RIFF"), 0, 4);
            stream.Write(BitConverter.GetBytes(36 + samplesCount * 2), 0, 4);
            stream.Write(System.Text.Encoding.UTF8.GetBytes("WAVE"), 0, 4);
            stream.Write(System.Text.Encoding.UTF8.GetBytes("fmt "), 0, 4);
            stream.Write(BitConverter.GetBytes(16), 0, 4);
            stream.Write(BitConverter.GetBytes((ushort)1), 0, 2);
            stream.Write(BitConverter.GetBytes((ushort)channels), 0, 2);
            stream.Write(BitConverter.GetBytes(hz), 0, 4);
            stream.Write(BitConverter.GetBytes(hz * channels * 2), 0, 4);
            stream.Write(BitConverter.GetBytes((ushort)(channels * 2)), 0, 2);
            stream.Write(BitConverter.GetBytes((ushort)16), 0, 2);
            stream.Write(System.Text.Encoding.UTF8.GetBytes("data"), 0, 4);
            stream.Write(BitConverter.GetBytes(samplesCount * 2), 0, 4);
        }
    }
}
