using NUnit.Framework;
using UnityEngine;

namespace net.rs64.TexTransTool.TestCode
{
    internal class UnityDiskUtilOwnershipTest
    {
        [Test]
        public void DisposeDoesNotDestroyBorrowedRuntimeTexture()
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            try
            {
                using (var diskUtil = new UnityDiskUtil(false))
                {
                    Assert.That(
                        diskUtil.GetOriginalTexture(texture),
                        Is.SameAs(texture));
                }

                Assert.That(texture == null, Is.False,
                    "UnityDiskUtil must not destroy a runtime Texture2D supplied by an upstream plugin.");
            }
            finally
            {
                if (texture != null)
                    Object.DestroyImmediate(texture);
            }
        }

#if UNITY_EDITOR_WIN
        [Test]
        public void DisposeDoesNotDestroyBorrowedPreloadedRuntimeTexture()
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            try
            {
                using (var diskUtil = new UnityDiskUtil(false))
                {
                    diskUtil.PreloadOriginalTexture(texture);
                }

                Assert.That(texture == null, Is.False,
                    "Pending async preload cleanup must not destroy an upstream runtime Texture2D.");
            }
            finally
            {
                if (texture != null)
                    Object.DestroyImmediate(texture);
            }
        }
#endif
    }
}
