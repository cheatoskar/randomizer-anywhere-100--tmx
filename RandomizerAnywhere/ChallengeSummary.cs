namespace RandomizerAnywhere;

internal readonly record struct ChallengeSummary(
    string Name,
    // the challenge's path inside the server's Tracks directory - the only thing that ties a
    // running challenge back to the TMX id we picked it by, see RandomizerGame.TrackIdFromFileName
    string? FileName,
    // real checkpoint blocks, with TMF's finish-line entry already taken off - see ToChallengeSummary
    int? NbCheckpoints,
    bool LapRace,
    int NbLaps,
    int AuthorTime,
    int GoldTime,
    int SilverTime,
    int BronzeTime);
