using Hart.MCP.Core.Native;

var point = NativeLibrary.project_seed_to_hypersphere(97);
Console.WriteLine($"Point: ({point.X}, {point.Y}, {point.Z}, {point.M})");
var hilbert = NativeLibrary.point_to_hilbert(point);
Console.WriteLine($"Hilbert: High={hilbert.High} Low={hilbert.Low}");
