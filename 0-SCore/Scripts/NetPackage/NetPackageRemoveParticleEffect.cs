﻿
public class NetPackageRemoveParticleEffect : NetPackage
{
    private Vector3i position;
    private int entityThatCausedIt;

    public NetPackageRemoveParticleEffect Setup(Vector3i _position, int _entityThatCausedIt)
    {
        this.position = _position;
        this.entityThatCausedIt = _entityThatCausedIt;
        return this;
    }


    public override void read(PooledBinaryReader _br)
    {
        this.position = new Vector3i((float)_br.ReadInt32(), (float)_br.ReadInt32(), (float)_br.ReadInt32());
        this.entityThatCausedIt = _br.ReadInt32();
    }

    public override void write(PooledBinaryWriter _bw)
    {
        base.write(_bw);
        _bw.Write((int)this.position.x);
        _bw.Write((int)this.position.y);
        _bw.Write((int)this.position.z); 
        _bw.Write(this.entityThatCausedIt);
    }

     public override int GetLength()
    {
        return 20;
    }

    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        if (_world == null)
        {
            return;
        }

        if (!_world.IsRemote())
        {
            BlockUtilitiesSDX.RemoveParticleEffectServer(this.position, this.entityThatCausedIt);
            return;
        }
       BlockUtilitiesSDX.RemoveParticleEffectClient(this.position, this.entityThatCausedIt);
    }
}

