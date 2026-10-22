using System;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WhalePaper.Services
{
    internal sealed class GifPlayer : IDisposable
    {
        private readonly Image _image;
        private readonly DispatcherTimer _timer;
        private GifAnimation _animation;
        private int _index;
        private bool _isPlaying;

        public GifPlayer(Image image)
        {
            _image = image;
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Tick += Timer_Tick;
        }

        public bool Load(string path)
        {
            Stop();
            _animation = GifAnimation.Load(path);
            if (_animation == null || _animation.Frames.Count == 0)
            {
                return false;
            }

            _index = 0;
            _image.Source = _animation.Frames[0];
            return true;
        }

        public void Play()
        {
            if (_animation == null || _animation.Frames.Count == 0)
            {
                return;
            }

            _isPlaying = true;
            ScheduleNext();
        }

        public void Pause()
        {
            _isPlaying = false;
            _timer.Stop();
        }

        public void Stop()
        {
            _isPlaying = false;
            _timer.Stop();
            _animation = null;
            _index = 0;
            if (_image != null)
            {
                _image.Source = null;
            }
        }

        private void ScheduleNext()
        {
            if (!_isPlaying || _animation == null)
            {
                return;
            }

            var delay = _animation.Delays[_index];
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30, delay));
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _timer.Stop();
            if (!_isPlaying || _animation == null || _animation.Frames.Count == 0)
            {
                return;
            }

            _index = (_index + 1) % _animation.Frames.Count;
            _image.Source = _animation.Frames[_index];
            ScheduleNext();
        }

        public void Dispose()
        {
            Stop();
            _timer.Tick -= Timer_Tick;
        }
    }
}
