using System;

namespace VerbalProcess
{
    /// <summary>
    /// VAD에서 추출된 음성 특징(Feature) 데이터를 담는 DTO
    /// </summary>
    [Serializable]
    public class FeatureData
    {
        public float speakingTime;
        public int pauseCount;
        public float averageVolume;

        public FeatureData(VoiceActivityDetector.VoiceFeatures features)
        {
            this.speakingTime = features.speakingTime;
            this.pauseCount = features.silenceCount;
            this.averageVolume = features.averageVolume;
        }
    }
}
