using System;
using System.Collections.Generic;

namespace Photon.Views;

/// <summary>
/// Payload passed from <see cref="GalleryView"/> to <see cref="ViewerPage"/>
/// so the viewer knows both the current item and the full ordered sibling
/// list (used for prev/next navigation and the filmstrip).
/// </summary>
public sealed record ViewerNavigationPayload(
    Models.MediaItem Current,
    IReadOnlyList<Models.MediaItem> Siblings);
