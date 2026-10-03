namespace DownKyi.Images;

internal sealed class SystemIcon
{
    public static SystemIcon Current { get; } = new();

    private SystemIcon()
    {
        Close = new VectorImage
        {
            Height = 12,
            Width = 12,
            Data =
                @"M25,23.9l-6.6-6.6c-0.3-0.3-0.9-0.3-1.1,0c-0.3,0.3-0.3,0.9,0,1.1l0,0l6.6,6.6l-6.6,6.6c-0.3,0.3-0.3,0.9,0,1.1
                         c0.3,0.3,0.9,0.3,1.1,0l0,0l6.6-6.6l6.6,6.6c0.3,0.3,0.9,0.3,1.1,0.1c0.3-0.3,0.3-0.9,0.1-1.1c0,0,0,0-0.1-0.1L26.1,25l6.6-6.6
                         c0.3-0.3,0.3-0.9,0-1.1c-0.3-0.3-0.9-0.3-1.1,0l0,0L25,23.9L25,23.9z",
            Fill = "#FF000000"
        };

        Info = new VectorImage
        {
            Height = 20,
            Width = 20,
            Data = @"M11 7h2v2h-2zm0 4h2v6h-2zm1-9C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0
                         18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z",
            Fill = "#FF00bcf2"
        };

        Warning = new VectorImage
        {
            Height = 20,
            Width = 20,
            Data = @"M12 5.99L19.53 19H4.47L12 5.99M2.74 18c-.77 1.33.19 3 1.73 3h15.06c1.54 0 2.5-1.67 1.73-3L13.73
                         4.99c-.77-1.33-2.69-1.33-3.46 0L2.74 18zM11 11v2c0 .55.45 1 1 1s1-.45 1-1v-2c0-.55-.45-1-1-1s-1 .45-1 1zm0 5h2v2h-2z",
            Fill = "#FFffb900"
        };

        Error = new VectorImage
        {
            Height = 20,
            Width = 20,
            Data = @"M12 7c.55 0 1 .45 1 1v4c0 .55-.45 1-1 1s-1-.45-1-1V8c0-.55.45-1 1-1zm-.01-5C6.47 2 2 6.48 2 12s4.47
                         10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zM12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8
                         8-3.58 8-8 8zm1-3h-2v-2h2v2z",
            Fill = "#FFd83b01"
        };
    }

    public VectorImage Close { get; private set; }
    public VectorImage Info { get; private set; }
    public VectorImage Warning { get; private set; }
    public VectorImage Error { get; private set; }
}
