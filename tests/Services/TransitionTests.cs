using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NUnit.Framework;
using ImageRotater.Controls;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // The transition setting reaches three renderers through one static, and
    // the settings page reaches the static through one converter. Both have
    // to agree on every value or a radio button silently selects nothing.
    [TestFixture]
    public class TransitionTests
    {
        private readonly EnumRadioConverter _converter = new EnumRadioConverter();

        [TestCase(TransitionStyle.Crossfade, false)]
        [TestCase(TransitionStyle.FadeThroughBlack, true)]
        [TestCase(TransitionStyle.FadeThroughWhite, true)]
        [TestCase(TransitionStyle.SlideFromRight, false)]
        [TestCase(TransitionStyle.Zoom, false)]
        [TestCase(TransitionStyle.Focus, false)]
        [TestCase(TransitionStyle.SideReveal, false)]
        [TestCase(TransitionStyle.DiagonalReveal, false)]
        [TestCase(TransitionStyle.DepthShift, false)]
        [TestCase(TransitionStyle.Mosaic, false)]
        [TestCase(TransitionStyle.Pixelate, false)]
        [TestCase(TransitionStyle.Cut, false)]
        public void Only_the_colour_styles_flash(TransitionStyle style, bool flash)
        {
            Assert.That(Transition.IsFlash(style), Is.EqualTo(flash));
        }

        [Test]
        public void White_is_white_and_everything_else_is_black()
        {
            Assert.That(Transition.FlashColor(TransitionStyle.FadeThroughWhite), Is.EqualTo(Colors.White));
            Assert.That(Transition.FlashColor(TransitionStyle.FadeThroughBlack), Is.EqualTo(Colors.Black));
        }

        [Test]
        public void Covers_and_backgrounds_are_chosen_independently()
        {
            Transition.CoverStyle = TransitionStyle.FadeThroughBlack;
            Transition.BackgroundStyle = TransitionStyle.Crossfade;
            Assert.That(Transition.CoverStyle, Is.Not.EqualTo(Transition.BackgroundStyle));
            Transition.CoverStyle = TransitionStyle.Crossfade;
        }

        [Test]
        public void Half_is_half_the_duration()
        {
            Assert.That(Transition.Half.TotalMilliseconds * 2, Is.EqualTo(Transition.Duration.TotalMilliseconds));
        }

        [TestCase("Crossfade", TransitionStyle.Crossfade)]
        [TestCase("FadeThroughBlack", TransitionStyle.FadeThroughBlack)]
        [TestCase("fadethroughwhite", TransitionStyle.FadeThroughWhite)]
        [TestCase("SlideFromRight", TransitionStyle.SlideFromRight)]
        [TestCase("Zoom", TransitionStyle.Zoom)]
        [TestCase("Focus", TransitionStyle.Focus)]
        [TestCase("SideReveal", TransitionStyle.SideReveal)]
        [TestCase("DiagonalReveal", TransitionStyle.DiagonalReveal)]
        [TestCase("DepthShift", TransitionStyle.DepthShift)]
        [TestCase("Mosaic", TransitionStyle.Mosaic)]
        [TestCase("Pixelate", TransitionStyle.Pixelate)]
        [TestCase("Cut", TransitionStyle.Cut)]
        public void Radio_converter_parses_every_transition_by_name(string parameter, TransitionStyle expected)
        {
            object back = _converter.ConvertBack(true, typeof(TransitionStyle), parameter, CultureInfo.InvariantCulture);
            Assert.That(back, Is.EqualTo(expected));

            object isChecked = _converter.Convert(expected, typeof(bool), parameter, CultureInfo.InvariantCulture);
            Assert.That(isChecked, Is.True);
        }

        [Test]
        public void Zoom_uses_its_own_transition_duration()
        {
            Assert.That(Transition.DurationFor(TransitionStyle.Zoom).TotalMilliseconds, Is.EqualTo(650));
        }

        [Test]
        public void Focus_uses_its_own_transition_duration()
        {
            Assert.That(Transition.DurationFor(TransitionStyle.Focus).TotalMilliseconds, Is.EqualTo(800));
        }


        [Test]
        public void Side_reveal_uses_its_own_transition_duration()
        {
            Assert.That(Transition.DurationFor(TransitionStyle.SideReveal).TotalMilliseconds, Is.EqualTo(650));
        }

        [Test]
        public void Diagonal_reveal_uses_its_own_transition_duration()
        {
            Assert.That(Transition.DurationFor(TransitionStyle.DiagonalReveal).TotalMilliseconds, Is.EqualTo(650));
        }

        [Test]
        public void Depth_shift_uses_its_own_transition_duration()
        {
            Assert.That(Transition.DurationFor(TransitionStyle.DepthShift).TotalMilliseconds, Is.EqualTo(700));
        }

        [Test]
        public void Mosaic_uses_its_own_transition_duration()
        {
            Assert.That(Transition.DurationFor(TransitionStyle.Mosaic).TotalMilliseconds, Is.EqualTo(700));
        }

        [Test]
        public void Pixelate_uses_its_own_transition_duration()
        {
            Assert.That(Transition.DurationFor(TransitionStyle.Pixelate).TotalMilliseconds, Is.EqualTo(1250));
        }

        [Test]
        public void Existing_transition_numeric_values_stay_compatible()
        {
            Assert.That((int)TransitionStyle.Crossfade, Is.EqualTo(0));
            Assert.That((int)TransitionStyle.FadeThroughBlack, Is.EqualTo(1));
            Assert.That((int)TransitionStyle.FadeThroughWhite, Is.EqualTo(2));
            Assert.That((int)TransitionStyle.SlideFromRight, Is.EqualTo(3));
            Assert.That((int)TransitionStyle.Cut, Is.EqualTo(4));
            Assert.That((int)TransitionStyle.Zoom, Is.EqualTo(5));
            Assert.That((int)TransitionStyle.Focus, Is.EqualTo(6));
            Assert.That((int)TransitionStyle.SideReveal, Is.EqualTo(7));
            Assert.That((int)TransitionStyle.DiagonalReveal, Is.EqualTo(8));
            Assert.That((int)TransitionStyle.DepthShift, Is.EqualTo(9));
            Assert.That((int)TransitionStyle.Mosaic, Is.EqualTo(10));
            Assert.That((int)TransitionStyle.Pixelate, Is.EqualTo(11));
        }

        [Test]
        public void Radio_converter_still_parses_selection_modes()
        {
            object back = _converter.ConvertBack(true, typeof(SelectionMode), "EverySelection", CultureInfo.InvariantCulture);
            Assert.That(back, Is.EqualTo(SelectionMode.EverySelection));
        }

        [Test]
        public void Unchecking_and_unknown_names_write_nothing()
        {
            Assert.That(_converter.ConvertBack(false, typeof(TransitionStyle), "Cut", CultureInfo.InvariantCulture),
                Is.EqualTo(Binding.DoNothing));
            Assert.That(_converter.ConvertBack(true, typeof(TransitionStyle), "Wipe", CultureInfo.InvariantCulture),
                Is.EqualTo(Binding.DoNothing));
        }
    }
}
