﻿using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System.Collections.Concurrent;
public class FireManager
{
    private static readonly string AdvFeatureClass = "FireManagement";
    static object locker = new object();

    private static FireManager instance = null;
    private static ConcurrentDictionary<Vector3i, BlockValue> FireMap = new ConcurrentDictionary<Vector3i, BlockValue>();
    public static ConcurrentDictionary<Vector3i, float> ExtinguishPositions = new ConcurrentDictionary<Vector3i, float>();
    private float checkTime = 120f;
    private float currentTime = 0f;
    private float fireDamage = 1f;
    private float smokeTime = 60f;
    private GameRandom random = new GameRandom();
    private float heatMapStrength = 0f;

    public string fireParticle;
    public string smokeParticle;
    private const string saveFile = "FireManager.dat";
    private ThreadManager.ThreadInfo dataSaveThreadInfo;

    private static BlockValue burntGround;

    public bool Enabled { private set; get; }
    public static FireManager Instance
    {
        get
        {
            return instance;
        }
    }
    public static void Init()
    {
        FireManager.instance = new FireManager();
        var option = Configuration.GetPropertyValue(AdvFeatureClass, "FireEnable");
        if (!StringParsers.ParseBool(option))
        {
            Log.Out("Fire Manager is disabled.");
            FireManager.Instance.Enabled = false;
            return;
        }
        FireManager.Instance.random = GameManager.Instance.World.GetGameRandom();

        FireManager.Instance.Enabled = true;
        option = Configuration.GetPropertyValue(AdvFeatureClass, "CheckInterval");
        if (!string.IsNullOrEmpty(option))
            FireManager.Instance.checkTime = StringParsers.ParseFloat(option);

        var strDamage = Configuration.GetPropertyValue(AdvFeatureClass, "FireDamage");
        if (!string.IsNullOrWhiteSpace(strDamage))
            FireManager.Instance.fireDamage = StringParsers.ParseFloat(strDamage);
        FireManager.Instance.currentTime = -1;

        var heatMap = Configuration.GetPropertyValue(AdvFeatureClass, "HeatMapStrength");
        if (!string.IsNullOrWhiteSpace(heatMap))
            FireManager.Instance.heatMapStrength = StringParsers.ParseFloat(heatMap);

        var smoke = Configuration.GetPropertyValue(AdvFeatureClass, "SmokeTime");
        if (!string.IsNullOrWhiteSpace(smoke))
            FireManager.Instance.smokeTime = StringParsers.ParseFloat(smoke);

        Log.Out("Starting Fire Manager");

        FireManager.Instance.fireParticle = Configuration.GetPropertyValue(AdvFeatureClass, "FireParticle");
        FireManager.Instance.smokeParticle = Configuration.GetPropertyValue(AdvFeatureClass, "SmokeParticle");
        burntGround = new BlockValue((uint)Block.GetBlockByName("terrBurntForestGround").blockID);

        // Register the particle effects before anything. This is causing the Unknown Particle warnings. 
        ParticleEffect.RegisterBundleParticleEffect(FireManager.Instance.fireParticle);
        ParticleEffect.RegisterBundleParticleEffect(FireManager.Instance.smokeParticle);

        // Read the FireManager
        FireManager.Instance.Load();

        ModEvents.GameShutdown.RegisterHandler(new Action(FireManager.Instance.CleanUp));

        // Only run the Update on the server, then just distribute the data to the clients using NetPackages.
        if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
        {
            Log.Out($" :: Fire Interval Check time: {FireManager.Instance.checkTime}");
            ModEvents.GameUpdate.RegisterHandler(new Action(FireManager.Instance.FireUpdate));
        }
    }



    // Poor man's timed cache
    public void CheckExtinguishedPosition()
    {
        var worldTime = GameManager.Instance.World.GetWorldTime();
        foreach (var position in ExtinguishPositions)
        {
            Remove(position.Key);
            if (position.Value < worldTime || GameManager.Instance.World.GetBlock(position.Key + Vector3i.down).isair)
            {
                ExtinguishPositions.TryRemove(position.Key, out var _);
                ClearPos(position.Key);
            }
        }
    }

    public void FireUpdate()
    {
        // Make sure to only run it once
        lock (locker)
        {
            currentTime -= Time.deltaTime;
            if (currentTime > 0f) return;

            CheckBlocks();
        }
    }

    public void CheckBlocks()
    {
        AdvLogging.DisplayLog(AdvFeatureClass, $"Checking Blocks for Fire: {FireMap.Count} Blocks registered. Extinguished Blocks: {ExtinguishPositions.Count}");
        currentTime = checkTime;


        var Changes = new List<BlockChangeInfo>();
        var neighbors = new List<Vector3i>();

        var alternate = false;
        CheckExtinguishedPosition();

        ChunkCluster chunkCluster = GameManager.Instance.World.ChunkClusters[0];
        if (chunkCluster == null) return;

        foreach (var posDict in FireMap)
        {
            var _blockPos = posDict.Key;
            if (!isFlammable(_blockPos))
            {
                Remove(_blockPos);
                continue;
            }

            // Get block specific damages
            var block = GameManager.Instance.World.GetBlock(_blockPos);
            if (block.Block.Properties.Contains("FireDamage"))
                block.damage += block.Block.Properties.GetInt("FireDamage");
            else
                block.damage += (int)fireDamage;

            if (alternate) // This follows the game rules more but is a heavier FPS hitter.
            {
                block.Block.DamageBlock(GameManager.Instance.World, 0, _blockPos, block, (int)fireDamage, -1);
            }
            else
            {
                if (block.damage >= block.Block.MaxDamage)
                {
                    block.Block.SpawnDestroyParticleEffect(GameManager.Instance.World, block, _blockPos, 1f, block.Block.tintColor, -1);
                    BlockValue blockValue2 = block.Block.DowngradeBlock;

                    // Check if there's another placeholder for this block.
                    if (!blockValue2.isair)
                        blockValue2 = BlockPlaceholderMap.Instance.Replace(blockValue2, GameManager.Instance.World.GetGameRandom(), _blockPos.x, _blockPos.z, false, QuestTags.none);
                    blockValue2.rotation = block.rotation;
                    blockValue2.meta = block.meta;
                    block = blockValue2;

                    // If there is terrain under it, convert it to burnt ground.
                    var blockBelow = GameManager.Instance.World.GetBlock(_blockPos + Vector3i.down);
                    if (blockBelow.Block.shape.IsTerrain())
                        Changes.Add(new BlockChangeInfo(0, _blockPos, burntGround));
                }

                if (!block.isair)
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(NetPackageManager.GetPackage<NetPackageAddFirePosition>().Setup(_blockPos, -1), false, -1, -1, -1, -1);

                Changes.Add(new BlockChangeInfo(0, _blockPos, block));
            }

            // If the new block has changed, check to make sure the new block is flammable. Note: it checks the blockValue, not blockPos, since the change hasn't been commited yet.
            if (!isFlammable(block) || block.isair)
            {
                Extinguish(_blockPos);
                continue;
            }

            if (!GameManager.Instance.HasBlockParticleEffect(_blockPos))
            {
                var _fireParticle = fireParticle;
                if (block.Block.Properties.Contains("FireParticle"))
                    _fireParticle = block.Block.Properties.GetString("FireParticle");

                BlockUtilitiesSDX.addParticlesCentered(_fireParticle, _blockPos);
            }
            // If we are damaging a block, allow the fire to spread.
            neighbors.AddRange(CheckNeighbors(_blockPos));

            FireMap[_blockPos] = block;
        }

        // Send all the changes in one shot
        GameManager.Instance.SetBlocksRPC(Changes);

        // Spread the fire to the neighbors. We delay this here so the fire does not spread too quickly or immediately, getting stuck in the above loop.
        foreach (var pos in neighbors)
            Add(pos);

        Save();
    }

    // Check to see if the nearby blocks can catch fire.
    public List<Vector3i> CheckNeighbors(Vector3i BlockPos)
    {
        var neighbors = new List<Vector3i>();
        foreach (var direction in Vector3i.AllDirections)
        {
            var position = BlockPos + direction;
            if (FireMap.ContainsKey(position))
                continue;
            if (isFlammable(position))
                neighbors.Add(position);
        }
        return neighbors;
    }

    public bool IsNearWater(Vector3i _blockPos)
    {
        foreach (var direction in Vector3i.AllDirections)
        {
            var position = _blockPos + direction;
            var blockValue = GameManager.Instance.World.GetBlock(position);
            if (blockValue.isWater) return true;
            if (blockValue.Block is BlockLiquidv2) return true;
        }
        return false;
    }

    public bool isFlammable(BlockValue blockValue)
    {
        if (blockValue.Block.HasAnyFastTags(FastTags.Parse("inflammable"))) return false;
        if (blockValue.ischild) return false;
        if (blockValue.isair) return false;
        if (blockValue.isWater) return false;

        if (blockValue.Block.HasAnyFastTags(FastTags.Parse("flammable"))) return true;
        var blockMaterial = blockValue.Block.blockMaterial;

        var matID = Configuration.GetPropertyValue(AdvFeatureClass, "MaterialID");
        if (matID.Contains(blockMaterial.id)) return true;
        var matDamage = Configuration.GetPropertyValue(AdvFeatureClass, "MaterialDamage");
        if (matDamage.Contains(blockMaterial.DamageCategory)) return true;

        var matSurface = Configuration.GetPropertyValue(AdvFeatureClass, "MaterialSurface");
        if (matSurface.Contains(blockMaterial.SurfaceCategory)) return true;

        return false;

    }
    public bool isFlammable(Vector3i _blockPos)
    {
        if (GameManager.Instance.World.IsWithinTraderArea(_blockPos)) return false;

        if (ExtinguishPositions.ContainsKey(_blockPos)) return false;

        // If its already burning, then don't do any other check
        if (isBurning(_blockPos)) return true;

        if (IsNearWater(_blockPos)) return false;

        // Check the block value.
        var blockValue = GameManager.Instance.World.GetBlock(_blockPos);
        if (isFlammable(blockValue))
            return true;

        return false;
    }

    public void Write(BinaryWriter _bw)
    {
        // Save the burning blocks.
        var writeOut = "";
        foreach (var temp in FireMap)
            writeOut += $"{temp.Key};";
        writeOut = writeOut.TrimEnd(';');
        _bw.Write(writeOut);

        // Save the blocks we've put out and put in a dampner
        var writeOut2 = "";
        foreach (var temp in ExtinguishPositions.Keys)
            writeOut2 += $"{temp};";
        writeOut2 = writeOut2.TrimEnd(';');
        _bw.Write(writeOut2);
    }

    public void Read(BinaryReader _br)
    {
        // Read burning blocks
        var positions = _br.ReadString();
        foreach (var position in positions.Split(';'))
        {
            if (string.IsNullOrEmpty(position)) continue;
            var vector = StringParsers.ParseVector3i(position);
            add(vector);
        }

        // Read extinguished blocks.
        var extingished = _br.ReadString();
        foreach (var position in extingished.Split(';'))
        {
            if (string.IsNullOrEmpty(position)) continue;
            var vector = StringParsers.ParseVector3i(position);
            extinguish(vector);
        }
    }


    public void ClearPos(Vector3i _blockPos)
    {
        if (!GameManager.IsDedicatedServer)
            BlockUtilitiesSDX.removeParticles(_blockPos);

        if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
        {
            SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(NetPackageManager.GetPackage<NetPackageRemoveParticleEffect>().Setup(_blockPos, -1), false);
            return;
        }
        SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(NetPackageManager.GetPackage<NetPackageRemoveParticleEffect>().Setup(_blockPos, -1), false, -1, -1, -1, -1);
    }
    public void Add(Vector3i _blockPos, int entityID = -1)
    {
        if (!isFlammable(_blockPos))
            return;

        if (!GameManager.IsDedicatedServer)
            add(_blockPos);

        if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
        {
            SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(NetPackageManager.GetPackage<NetPackageAddFirePosition>().Setup(_blockPos, entityID), false);
            return;
        }
        SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(NetPackageManager.GetPackage<NetPackageAddFirePosition>().Setup(_blockPos, entityID), false, -1, -1, -1, -1);

    }


    // General call to remove the fire from a block, and add an extinguished counter, so blocks can be temporarily immune to restarting.
    public void Extinguish(Vector3i _blockPos, int entityID = -1)
    {
        if (!GameManager.IsDedicatedServer)
            extinguish(_blockPos, entityID);

        if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
        {
            SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(NetPackageManager.GetPackage<NetPackageAddExtinguishPosition>().Setup(_blockPos, entityID), false);
            return;
        }
        SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(NetPackageManager.GetPackage<NetPackageAddExtinguishPosition>().Setup(_blockPos, entityID), false, -1, -1, -1, -1);
    }
    public void Remove(Vector3i _blockPos, int entityID = -1)
    {
        if (!GameManager.IsDedicatedServer)
            remove(_blockPos);

        if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
        {
            SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(NetPackageManager.GetPackage<NetPackageRemoveFirePosition>().Setup(_blockPos, entityID), false);
            return;
        }
        SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(NetPackageManager.GetPackage<NetPackageRemoveFirePosition>().Setup(_blockPos, entityID), false, -1, -1, -1, -1);
    }


    public void remove(Vector3i _blockPos)
    {
        if (!FireMap.ContainsKey(_blockPos)) return;

        BlockUtilitiesSDX.removeParticles(_blockPos);
        FireMap.TryRemove(_blockPos, out var block);
    }

    public void extinguish(Vector3i _blockPos, int entityID = -1)
    {
        var worldTime = GameManager.Instance.World.GetWorldTime();
        var expiry = worldTime + smokeTime;
        if (!ExtinguishPositions.ContainsKey(_blockPos))
            ExtinguishPositions[_blockPos] = expiry;

        var block = GameManager.Instance.World.GetBlock(_blockPos);
        var _smokeParticle = smokeParticle;
        if (block.Block.Properties.Contains("SmokeParticle"))
            _smokeParticle = block.Block.Properties.GetString("SmokeParticle");

        if (!block.isair)
            BlockUtilitiesSDX.addParticlesCentered(_smokeParticle, _blockPos);

    }


    // Add flammable blocks to the Fire Map
    public void add(Vector3i _blockPos)
    {
        var block = GameManager.Instance.World.GetBlock(_blockPos);

        var _fireParticle = fireParticle;
        if (block.Block.Properties.Contains("FireParticle"))
            _fireParticle = block.Block.Properties.GetString("FireParticle");

        BlockUtilitiesSDX.addParticlesCentered(_fireParticle, _blockPos);

        if (FireMap.TryAdd(_blockPos, block))
        {
            if (heatMapStrength != 0)
                GameManager.Instance.World?.aiDirector?.NotifyActivity(EnumAIDirectorChunkEvent.Campfire, _blockPos, block.Block.HeatMapStrength, (ulong)block.Block.HeatMapTime);
        }
    }

    public bool isBurning(Vector3i _blockPos)
    {
        return FireMap.ContainsKey(_blockPos);
    }
    private int saveDataThreaded(ThreadManager.ThreadInfo _threadInfo)
    {
        PooledExpandableMemoryStream pooledExpandableMemoryStream = (PooledExpandableMemoryStream)_threadInfo.parameter;
        string text = string.Format("{0}/{1}", GameIO.GetSaveGameDir(), saveFile);
        if (!Directory.Exists(GameIO.GetSaveGameDir()))
        {
            return -1;
        }
        if (File.Exists(text))
        {
            File.Copy(text, string.Format("{0}/{1}", GameIO.GetSaveGameDir(), $"{saveFile}.bak"), true);
        }
        pooledExpandableMemoryStream.Position = 0L;
        StreamUtils.WriteStreamToFile(pooledExpandableMemoryStream, text);
        MemoryPools.poolMemoryStream.FreeSync(pooledExpandableMemoryStream);
        Log.Out($"Fire Manager {text} Saving: {FireMap.Count}");

        return -1;
    }

    public void Save()
    {
        if (this.dataSaveThreadInfo == null || !ThreadManager.ActiveThreads.ContainsKey("silent_FireDataSave"))
        {
            PooledExpandableMemoryStream pooledExpandableMemoryStream = MemoryPools.poolMemoryStream.AllocSync(true);
            using (PooledBinaryWriter pooledBinaryWriter = MemoryPools.poolBinaryWriter.AllocSync(false))
            {
                pooledBinaryWriter.SetBaseStream(pooledExpandableMemoryStream);
                this.Write(pooledBinaryWriter);
            }


            this.dataSaveThreadInfo = ThreadManager.StartThread("silent_FireDataSave", null, new ThreadManager.ThreadFunctionLoopDelegate(this.saveDataThreaded), null, System.Threading.ThreadPriority.Normal, pooledExpandableMemoryStream, null, false);
        }
    }

    public void Load()
    {
        string path = string.Format("{0}/{1}", GameIO.GetSaveGameDir(), saveFile);
        if (Directory.Exists(GameIO.GetSaveGameDir()) && File.Exists(path))
        {
            try
            {
                using (FileStream fileStream = File.OpenRead(path))
                {
                    using (PooledBinaryReader pooledBinaryReader = MemoryPools.poolBinaryReader.AllocSync(false))
                    {
                        pooledBinaryReader.SetBaseStream(fileStream);
                        this.Read(pooledBinaryReader);
                    }
                }
            }
            catch (Exception)
            {
                path = string.Format("{0}/{1}", GameIO.GetSaveGameDir(), $"{saveFile}.bak");
                if (File.Exists(path))
                {
                    using (FileStream fileStream2 = File.OpenRead(path))
                    {
                        using (PooledBinaryReader pooledBinaryReader2 = MemoryPools.poolBinaryReader.AllocSync(false))
                        {
                            pooledBinaryReader2.SetBaseStream(fileStream2);
                            this.Read(pooledBinaryReader2);
                        }
                    }
                }
            }

            Log.Out($"Fire Manager {path} Loaded: {FireMap.Count}");
        }
    }

    public void CleanUp()
    {
        if (FireMap.Count > 0 || ExtinguishPositions.Count > 0)
        {
            // Only save if we have data to save.
            this.WaitOnSave();
            this.Save();
            this.WaitOnSave();
        }
        Log.Out("Fire Manager Clean up");
        FireMap.Clear();
        ExtinguishPositions.Clear();
        FireManager.instance = null;


    }

    public void Reset()
    {
        Log.Out("Removing all blocks that are on fire and smoke.");
        lock (locker)
        {
            foreach (var position in FireMap.Keys)
                Remove(position);

            foreach (var position in ExtinguishPositions.Keys)
                BlockUtilitiesSDX.removeParticles(position);

            FireMap.Clear();
            ExtinguishPositions.Clear();
            Save();
        }
    }
    private void WaitOnSave()
    {
        if (this.dataSaveThreadInfo != null)
        {
            this.dataSaveThreadInfo.WaitForEnd();
            this.dataSaveThreadInfo = null;
        }
    }
}

