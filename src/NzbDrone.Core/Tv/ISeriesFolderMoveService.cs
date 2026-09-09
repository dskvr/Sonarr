using System.Collections.Generic;

namespace NzbDrone.Core.Tv
{
    public interface ISeriesFolderMoveService
    {
        void Move(Series series, string sourcePath, string destinationPath);
        void Recover(Series series);
        bool HasPendingMove(int seriesId);
        void RecoverPending(IEnumerable<int> seriesIds, IEnumerable<string> paths);
    }
}
