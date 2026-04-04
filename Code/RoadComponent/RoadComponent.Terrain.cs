using System;
using System.Linq;
using Sandbox;

namespace RedSnail.RoadTool;

public partial class RoadComponent
{
	[Property, FeatureEnabled( "Terrain", Icon = "landscape", Tint = EditorTint.Green )]
	private bool HasTerrainModification { get; set; } = false;

	[Property, Feature( "Terrain" ), Hide]
	private Terrain TerrainTarget { get; set; }

	[Property, Feature( "Terrain" ), Range( 0f, 2000f )]
	public float TerrainFalloffRadius { get; set; } = 500f;

	[Property, Feature( "Terrain" ), Range( 10f, 500f )]
	public float TerrainStepPrecision { get; set; } = 50f;

	[Property, Feature( "Terrain" ), Range( -10f, 10f )]
	public float TerrainHeightOffset { get; set; } = 0f;

	/// <summary>
	/// Adapts the terrain geometry to the spline shape.
	/// </summary>
	public void AdaptTerrainToRoad()
	{
		if ( !TerrainTarget.IsValid() )
		{
			TerrainTarget = Scene.GetAllComponents<Terrain>().FirstOrDefault();
		}

		if ( !TerrainTarget.IsValid() )
		{
			Log.Warning( "RoadTool: No valid TerrainTarget found in scene." );
			return;
		}

		if ( Spline == null || Spline.PointCount < 2 )
			return;

		var storage = TerrainTarget.Storage;
		if ( storage == null || storage.HeightMap == null )
		{
			Log.Warning( "RoadTool: Terrain Storage or HeightMap is missing." );
			return;
		}

		// 1. Terrain and Road Parameters
		int resolution = storage.Resolution;
		float terrainSize = storage.TerrainSize;
		float terrainMaxHeight = storage.TerrainHeight;
		float halfSize = terrainSize * 0.5f;
		float roadWidthHalf = RoadWidth * 0.5f;
		int sampleCount = Math.Max( 1, (int)MathF.Ceiling( Spline.Length / Math.Max( 5f, TerrainStepPrecision ) ) );

		// 2. Initialization of calculation buffers
		var heightMap = storage.HeightMap;

		var updatedHeights = new float[heightMap.Length];
		var bestDistance = new float[heightMap.Length];

		for ( int i = 0; i < heightMap.Length; i++ )
		{
			updatedHeights[i] = (heightMap[i] / (float)ushort.MaxValue) * terrainMaxHeight;
			bestDistance[i] = float.MaxValue;
		}

		// 3. Spline Sampling
		var frames = UseRotationMinimizingFrames
			? CalculateRotationMinimizingTangentFrames( Spline, sampleCount + 1 )
			: CalculateTangentFramesUsingUpDir( Spline, sampleCount + 1 );

		bool hasModified = false;

		for ( int i = 0; i <= sampleCount; i++ )
		{
			var frame = frames[i];
			var worldPos = WorldTransform.PointToWorld( frame.Position );
			var worldRight = WorldRotation * frame.Rotation.Right;

			// Conversion to local terrain coordinates to support rotation/translation
			var localPos = TerrainTarget.Transform.World.PointToLocal( worldPos );
			var roadRightLocal = TerrainTarget.Transform.World.Rotation.Inverse * worldRight;

			// Adaptive coordinate system detection (Center vs Corner)
			float u = localPos.x / terrainSize;
			float v = localPos.y / terrainSize;
			bool localIsCentered = false;

			if ( u < 0f || u > 1f || v < 0f || v > 1f )
			{
				u = (localPos.x + halfSize) / terrainSize;
				v = (localPos.y + halfSize) / terrainSize;
				localIsCentered = true;
			}

			if ( u < 0f || u > 1f || v < 0f || v > 1f ) continue;

			int gridX = Math.Clamp( (int)MathF.Round( u * (resolution - 1) ), 0, resolution - 1 );
			int gridY = Math.Clamp( (int)MathF.Round( v * (resolution - 1) ), 0, resolution - 1 );

			float cellSize = terrainSize / (resolution - 1);
			float totalRadius = roadWidthHalf + TerrainFalloffRadius;
			int pixelRadius = (int)MathF.Ceiling( totalRadius / cellSize );

			var roadRight2D = new Vector2( roadRightLocal.x, roadRightLocal.y );
			if ( roadRight2D.LengthSquared > 0.0001f )
			{
				roadRight2D = roadRight2D.Normal;
			}
			else
			{
				roadRight2D = new Vector2( 1f, 0f );
			}
			Vector3 roadCenter = localPos.WithZ( 0 );

			// Modify pixels within influence radius
			for ( int ix = gridX - pixelRadius; ix <= gridX + pixelRadius; ix++ )
			{
				for ( int iy = gridY - pixelRadius; iy <= gridY + pixelRadius; iy++ )
				{
					if ( ix < 0 || ix >= resolution || iy < 0 || iy >= resolution ) continue;

					// s&box indexing: ix (World X) is major axis, iy (World Y) is minor axis
					// Use iy * resolution + ix to match standard storage if ix*res doesn't work
					int index = iy * resolution + ix;

					float nodeLocalX = (ix / (float)(resolution - 1)) * terrainSize - (localIsCentered ? halfSize : 0);
					float nodeLocalY = (iy / (float)(resolution - 1)) * terrainSize - (localIsCentered ? halfSize : 0);

					Vector3 nodeLocalPos = new Vector3( nodeLocalX, nodeLocalY, 0 );
					float distance = Vector3.DistanceBetween( roadCenter, nodeLocalPos );

					if ( distance > totalRadius ) continue;

					// Height calculation with Roll and Offset
					var nodeLocal2D = new Vector2( nodeLocalX - localPos.x, nodeLocalY - localPos.y );
					float lateral = Vector2.Dot( nodeLocal2D, roadRight2D );
					float rollHeightOffset = roadRightLocal.z * lateral;
					float roadCoreHeight = Math.Clamp( localPos.z + TerrainHeightOffset + rollHeightOffset, 0f, terrainMaxHeight );

					float candidateHeight;
					if ( distance <= roadWidthHalf )
					{
						candidateHeight = roadCoreHeight;
					}
					else
					{
						float t = Math.Clamp( (distance - roadWidthHalf) / TerrainFalloffRadius, 0f, 1f );
						float smoothT = t * t * (3f - 2f * t);
						candidateHeight = MathX.Lerp( roadCoreHeight, (heightMap[index] / (float)ushort.MaxValue) * terrainMaxHeight, smoothT );
					}

					if ( distance < bestDistance[index] )
					{
						bestDistance[index] = distance;
						updatedHeights[index] = candidateHeight;
						hasModified = true;
					}
				}
			}
		}

		if ( hasModified )
		{
			// 4. Final encoding to ushort and GPU synchronization
			for ( int i = 0; i < heightMap.Length; i++ )
			{
				heightMap[i] = (ushort)MathF.Round( Math.Clamp( updatedHeights[i], 0f, terrainMaxHeight ) / terrainMaxHeight * ushort.MaxValue );
			}

			storage.HeightMap = heightMap;
			storage.StateHasChanged();
			TerrainTarget.Create();
			TerrainTarget.SyncGPUTexture();

			Log.Info( "RoadTool: Terrain terraformed successfully!" );
		}
	}
}