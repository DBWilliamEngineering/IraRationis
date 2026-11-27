// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-loader/src/thunks.rs.


namespace IraRationis.Loader.Imports.Models;

internal sealed class ImportThunksSite(MissingImport import, UnimplementedPolicy policy)
{
    public int Calls;

    public MissingImport Import
    {
        get;
    } = import;

    public UnimplementedPolicy Policy
    {
        get;
    } = policy;
}
