using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Animated artwork only ever moves in the plugin's own controls.
    //
    // The write path cannot animate: Playnite decodes Game.BackgroundImage and
    // Game.CoverImage to a single BitmapSource, so BackgroundRotationService
    // stores a first-frame poster instead. That is correct, and it means the
    // ONLY place a GIF plays is a control that hands the file to
    // XamlAnimatedGif itself.
    //
    // Backgrounds went a long time without that branch - a downloaded animated
    // background showed its poster frame and nothing else, with no setting that
    // could change it, because the code to animate one did not exist. These
    // tests keep both controls honest.
    [TestFixture]
    public class AnimatedRenderPathTests
    {
        private static string ControlSource(string fileName)
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Controls")))
            {
                dir = dir.Parent;
            }

            Assert.IsNotNull(dir, "could not locate src\\Controls from the test directory");
            string path = Path.Combine(dir.FullName, "src", "Controls", fileName);

            Assert.IsTrue(File.Exists(path), $"{fileName} not found at {path}");
            return File.ReadAllText(path);
        }

        // Both controls must hand animated files to XamlAnimatedGif. Without
        // this the control silently degrades to a still frame, which looks
        // exactly like a correctly-written poster and so goes unnoticed.
        [TestCase("BackgroundImageControl.xaml.cs")]
        [TestCase("CoverImageControl.xaml.cs")]
        public void ControlAnimatesRatherThanDecodingAStillFrame(string file)
        {
            string source = ControlSource(file);

            StringAssert.Contains("AnimationBehavior.SetSourceUri", source,
                $"{file} never hands a file to XamlAnimatedGif, so animated artwork "
                + "can only ever render as a still frame");

            StringAssert.Contains("PosterFrame.IsAnimated", source,
                $"{file} must branch on the same predicate the write path uses, or the "
                + "two disagree about which files are animated");
        }

        // The attached property owns Image.Source while it is set. A control
        // that never clears it keeps decoding frames after the pick changes -
        // the previous game's GIF plays on under the next game's artwork, and
        // an unloaded control animates forever off-screen.
        [Test]
        public void CoverControlReleasesTheAnimationWhenThePickIsNotAnimated()
        {
            string source = ControlSource("CoverImageControl.xaml.cs");

            int clears = Regex.Matches(source, @"SetSourceUri\(\s*DisplayImage\s*,\s*null\s*\)").Count;

            Assert.GreaterOrEqual(clears, 2,
                $"the cover control clears the animation {clears} time(s). It must be released both "
                + "when a static pick replaces an animated one and when the control "
                + "unloads, or frames keep decoding under the next image");
        }

        // Backgrounds render into two alternating slots. ClearSlot releases a
        // slot's animation; it runs on the outgoing slot after every swap and
        // on both slots when the control unloads.
        [Test]
        public void BackgroundControlReleasesTheAnimationOfEverySlot()
        {
            string source = ControlSource("BackgroundImageControl.xaml.cs");

            StringAssert.IsMatch(@"SetSourceUri\(\s*slot\.Image\s*,\s*null\s*\)", source,
                "ClearSlot must release the slot's animation");
            StringAssert.IsMatch(@"void OnUnloaded[\s\S]*?ClearSlot\(_slotA\);\s*ClearSlot\(_slotB\);", source,
                "unloading must release both slots");
            Assert.GreaterOrEqual(Regex.Matches(source, @"ClearSlot\(outgoing\)").Count, 1,
                "a swap must release the slot it leaves");
        }

        // The predicate both controls branch on. Extension-based on purpose:
        // it is the saved extension the download path guarantees, and it must
        // stay in step with what the controls can actually play.
        [Test]
        public void OnlyGifIsTreatedAsPlayable()
        {
            Assert.IsTrue(PosterFrame.IsAnimated(@"C:\art\clip.gif"));
            Assert.IsTrue(PosterFrame.IsAnimated(@"C:\art\CLIP.GIF"), "extension match is case-insensitive");

            // These animate in principle but XamlAnimatedGif does not play them,
            // so treating them as animated would strand them: the write path
            // would try to poster a file it cannot decode and drop the pick.
            Assert.IsFalse(PosterFrame.IsAnimated(@"C:\art\clip.webp"));
            Assert.IsFalse(PosterFrame.IsAnimated(@"C:\art\clip.apng"));
            Assert.IsFalse(PosterFrame.IsAnimated(@"C:\art\clip.png"));
            Assert.IsFalse(PosterFrame.IsAnimated(null));
        }
    }
}
